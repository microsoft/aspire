import * as vscode from 'vscode';
import * as path from 'path';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isAzureFunctionsLaunchConfiguration } from '../../dcp/types';
import { azureFunctionsCmdDelayedExpansion, azureFunctionsCmdPercentArgument, azureFunctionsUnsupportedTaskShell, csharpDebuggerName, invalidLaunchConfiguration } from '../../loc/strings';
import { assertNoTerminalControlCharacters, quoteShellArg } from '../../utils/AspireTerminalProvider';
import { quoteCmdArgument } from '../../utils/cmdShim';
import { extensionLogOutputChannel } from '../../utils/logging';
import { AlreadyStartedResourceDebugSession, ResourceDebuggerExtension } from '../debuggerExtensions';
import { DotNetService } from './dotnet';
import { cleanupRun, registerRunCleanup } from '../runCleanupRegistry';

const AF_EXTENSION_ID = 'ms-azuretools.vscode-azurefunctions';

/**
 * Result from the Azure Functions extension's startFuncProcess API.
 * processId is a string — it's the PID of the dotnet worker process
 * (found via pickChildProcess which searches for a child matching /(dotnet|func)/).
 */
interface StartFuncProcessResult {
    processId: string;
    success: boolean;
    error?: string;
}

/**
 * The Azure Functions extension API (v1.10.0).
 * Obtained via the @microsoft/vscode-azext-utils API provider pattern:
 *   ext.exports.getApi('~1.10.0') → AzureFunctionsApi
 */
interface AzureFunctionsApi {
    apiVersion: string;
    startFuncProcess(buildPath: string, args: string[], env: Record<string, string>): Promise<StartFuncProcessResult>;
}

interface AzureFunctionsApiProvider {
    getApi(apiVersion: string): AzureFunctionsApi;
}

type FuncHostTaskShell = 'cmd' | 'fish' | 'powershell' | 'posix';

type TerminalProfileConfiguration = {
    path?: string | string[];
    source?: string;
};

/** Tracks worker PIDs by runId for cleanup. */
const workerPidsByRunId = new Map<string, number>();

/** Tracks the VS Code Task executions (func host start) by runId for cleanup. */
const taskExecutionsByRunId = new Map<string, vscode.TaskExecution>();

/** Kill the func host task and worker process for the given runId, if any. */
function killFuncProcess(runId: string): void {
    // Terminate the VS Code Task running "func host start"
    const taskExecution = taskExecutionsByRunId.get(runId);
    if (taskExecution) {
        extensionLogOutputChannel.info(`Terminating func host task for runId ${runId}`);
        taskExecution.terminate();
        taskExecutionsByRunId.delete(runId);
    }

    // Also kill the worker PID directly in case task termination doesn't propagate
    const pid = workerPidsByRunId.get(runId);
    if (pid !== undefined) {
        extensionLogOutputChannel.info(`Killing func worker process for runId ${runId} (pid: ${pid})`);
        try {
            process.kill(pid);
        } catch {
            // Process may already be dead
        }
        workerPidsByRunId.delete(runId);
    }
}

async function getAzureFunctionsApi(): Promise<AzureFunctionsApi> {
    const ext = vscode.extensions.getExtension(AF_EXTENSION_ID);
    if (!ext) {
        throw new Error(`Azure Functions extension (${AF_EXTENSION_ID}) is not installed`);
    }
    if (!ext.isActive) {
        await ext.activate();
    }

    // The AF extension uses the @microsoft/vscode-azext-utils API provider
    // pattern. ext.exports has a getApi(version) method that returns the actual API.
    const provider = ext.exports as AzureFunctionsApiProvider;
    if (typeof provider?.getApi !== 'function') {
        throw new Error('Azure Functions extension does not expose the expected getApi provider');
    }

    return provider.getApi('~1.10.0');
}

function isFuncHostTaskForBuildPath(task: vscode.Task, buildOutputPath: string): boolean {
    // Azure Functions API v1.10 starts tasks as:
    //   source: "func"
    //   ShellExecution.commandLine: "func host start ..."
    //   ShellExecution.options.cwd: <build output path>
    // See https://github.com/microsoft/vscode-azurefunctions/blob/v1.22.0/src/commands/pickFuncProcess.ts
    const execution = task.execution as vscode.ShellExecution | undefined;
    return task.source === 'func' &&
        execution?.options?.cwd === buildOutputPath &&
        typeof execution.commandLine === 'string' &&
        /^func(?:\.exe)?\s+host\s+start(?:\s|$)/i.test(execution.commandLine);
}

function quoteFuncHostArguments(args: string[] | undefined): string[] {
    const funcHostArgs = args ?? [];
    for (const argument of funcHostArgs) {
        assertNoTerminalControlCharacters(argument);
    }

    // These characters have the same literal meaning in the supported task shells.
    // Avoid resolving the configured shell when no argument needs shell-specific quoting.
    if (funcHostArgs.every(argument => /^[A-Za-z0-9_./:-]+$/.test(argument))) {
        return funcHostArgs;
    }

    const shell = getFuncHostTaskShell();
    return funcHostArgs.map(argument => quoteFuncHostArgument(argument, shell));
}

function quoteFuncHostArgument(argument: string, shell: FuncHostTaskShell): string {
    // Keep ordinary flags and paths unchanged so the Azure Functions extension can
    // still inspect exact flag values before it flattens the array for ShellExecution.
    const isShellSafe = shell === 'posix' || shell === 'fish'
        ? /^[A-Za-z0-9_./:-]+$/.test(argument)
        : /^[A-Za-z0-9_./:\\-]+$/.test(argument);
    if (isShellSafe) {
        return argument;
    }

    if (shell === 'cmd') {
        // cmd.exe expands %NAME% even inside double quotes. There is no command-line
        // escape that preserves an arbitrary percent sequence before a .cmd shim runs.
        if (argument.includes('%')) {
            throw new Error(azureFunctionsCmdPercentArgument);
        }

        // Delayed expansion can be enabled by the terminal profile or the Command
        // Processor registry settings. No quoting form preserves arbitrary !
        // sequences through a .cmd shim under both expansion modes.
        if (argument.includes('!')) {
            throw new Error(azureFunctionsCmdDelayedExpansion);
        }

        return quoteCmdArgument(argument);
    }

    if (shell === 'fish') {
        // Fish only recognizes \' and \\ inside single quotes, so escape both before
        // wrapping the argument. See https://fishshell.com/docs/current/language.html#quotes.
        return `'${argument.replace(/[\\']/g, value => `\\${value}`)}'`;
    }

    return quoteShellArg(argument, shell === 'powershell' ? 'win32' : 'linux');
}

function getFuncHostTaskShell(): FuncHostTaskShell {
    const platform = process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'osx' : 'linux';
    const terminalConfiguration = vscode.workspace.getConfiguration('terminal.integrated');
    const automationProfile = terminalConfiguration.get<TerminalProfileConfiguration | null>(`automationProfile.${platform}`);
    if (automationProfile) {
        return classifyFuncHostTaskShell(automationProfile) ?? throwUnsupportedTaskShell();
    }

    const defaultProfileName = terminalConfiguration.get<string>(`defaultProfile.${platform}`);
    if (defaultProfileName) {
        const profiles = terminalConfiguration.get<Record<string, TerminalProfileConfiguration | null>>(`profiles.${platform}`);
        const defaultProfile = profiles?.[defaultProfileName] ?? undefined;
        return classifyFuncHostTaskShell(defaultProfile, defaultProfileName) ?? throwUnsupportedTaskShell();
    }

    if (process.platform === 'win32') {
        // PowerShell is VS Code's Windows task-shell default when no automation or
        // default profile is configured.
        return 'powershell';
    }

    const loginShell = process.env.SHELL;
    if (!loginShell) {
        return 'posix';
    }

    return classifyFuncHostTaskShell({ path: loginShell }) ?? throwUnsupportedTaskShell();
}

function classifyFuncHostTaskShell(profile: TerminalProfileConfiguration | undefined, profileName?: string): FuncHostTaskShell | undefined {
    const paths = typeof profile?.path === 'string' ? [profile.path] : profile?.path ?? [];
    const identity = [profileName, profile?.source, ...paths].filter((value): value is string => !!value).join(' ').toLowerCase();

    if (identity.includes('powershell') || identity.includes('pwsh')) {
        return 'powershell';
    }

    if (identity.includes('command prompt') || /(?:^|[\\/\s])cmd(?:\.exe)?(?:$|\s)/.test(identity)) {
        return 'cmd';
    }

    if (/(?:^|[\\/\s])fish(?:\.exe)?(?:$|\s)/.test(identity)) {
        return 'fish';
    }

    if (identity.includes('git bash') || identity.includes('wsl') || identity.includes('cygwin') || identity.includes('msys') ||
        /(?:^|[\\/\s])(ba|z|fi|k)?sh(?:\.exe)?(?:$|\s)/.test(identity)) {
        return 'posix';
    }

    return undefined;
}

function throwUnsupportedTaskShell(): never {
    throw new Error(azureFunctionsUnsupportedTaskShell);
}

export const azureFunctionsDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'azure-functions',
    debugAdapter: 'coreclr',
    extensionId: 'ms-dotnettools.csharp',
    extensionDisplayName: csharpDebuggerName,
    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => {
        if (isAzureFunctionsLaunchConfiguration(launchConfig) && launchConfig.project_path) {
            return `Azure Functions: ${path.basename(launchConfig.project_path)}`;
        }
        return 'Azure Functions';
    },
    getSupportedFileTypes: () => ['.cs', '.csproj'],
    getProjectFile: (launchConfig) => {
        if (isAzureFunctionsLaunchConfiguration(launchConfig)) {
            return launchConfig.project_path;
        }
        throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
    },
    createDebugSessionConfigurationCallback: async (launchConfig, args, env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<AlreadyStartedResourceDebugSession | void> => {
        if (!isAzureFunctionsLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not azure-functions for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        // Register cleanup for this run up-front so that killFuncProcess is called
        // via the generic cleanupRun path regardless of how the session ends.
        registerRunCleanup(debugConfiguration.runId, () => killFuncProcess(debugConfiguration.runId));

        const projectPath = launchConfig.project_path;
        const dotNetService = new DotNetService(launchOptions.debugSession);
        // project_path from the hosting integration is currently a .csproj file path
        // (resolved by AzureFunctionsProjectMetadata.ResolveProjectPath). If Aspire
        // later supports non-.NET Functions resources, that launch config should carry
        // an explicit language/build contract instead of reusing this .NET project path.
        // The AF extension API expects the project build output as buildPath.
        // Always build because path-based Functions resources do not have to be ProjectReferences
        // of the AppHost, so an existing target can be stale even after the AppHost was rebuilt.
        extensionLogOutputChannel.info(`Building Azure Functions project before starting via extension API: ${projectPath}`);
        await dotNetService.buildDotNetProject(projectPath);
        const targetPath = await dotNetService.getDotNetTargetPath(projectPath);
        const buildOutputPath = path.dirname(targetPath);
        extensionLogOutputChannel.info(`Starting Azure Functions project via extension API: ${projectPath} (buildPath: ${buildOutputPath})`);

        // Only pass DCP-specific env vars to the AF extension. The VS Code Task
        // it creates already inherits the VS Code process environment, so we
        // don't need to merge process.env — that would just duplicate values.
        const dcpEnv = Object.fromEntries(
            (env ?? []).filter(e => e.value !== undefined).map(e => [e.name, e.value])
        );

        // Start func host via the Azure Functions extension API.
        // The API creates a VS Code Task running "func host start" from the
        // build output path, polls /admin/host/status until ready, then finds
        // the dotnet worker child process and returns its PID.
        //
        // The AF extension API has no stopFuncProcess method. Register before calling
        // startFuncProcess because that API waits for host readiness before returning.
        const api = await getAzureFunctionsApi();
        extensionLogOutputChannel.info(`Got Azure Functions API (version ${api.apiVersion}), calling startFuncProcess`);

        let funcExecution: vscode.TaskExecution | undefined;
        let pendingFuncExitCode: number | undefined;
        let completeFuncSession: ((exitCode: number) => void) | undefined;
        const captureFuncExecution = (execution: vscode.TaskExecution): void => {
            if (funcExecution) {
                return;
            }

            funcExecution = execution;
            extensionLogOutputChannel.info(`Captured func host task for runId ${debugConfiguration.runId}: ${execution.task.name}`);
            taskExecutionsByRunId.set(debugConfiguration.runId, execution);
        };
        const taskStartSubscription = vscode.tasks.onDidStartTaskProcess(event => {
            if (isFuncHostTaskForBuildPath(event.execution.task, buildOutputPath)) {
                captureFuncExecution(event.execution);
            }
        });
        const taskEndSubscription = launchOptions.debug ? undefined : vscode.tasks.onDidEndTaskProcess(event => {
            if (event.execution !== funcExecution) {
                return;
            }

            let exitCode = event.exitCode ?? 0;
            // Exit code 143 is SIGTERM on macOS and Linux, matching the normal
            // debug-adapter termination path in adapterTracker.
            if ((process.platform === 'darwin' || process.platform === 'linux') && exitCode === 143) {
                exitCode = 0;
            }

            if (completeFuncSession) {
                completeFuncSession(exitCode);
            } else {
                // startFuncProcess waits for readiness. Preserve an exit that races with
                // its return so the already-started session still terminates correctly.
                pendingFuncExitCode = exitCode;
            }
        });

        let result: StartFuncProcessResult;
        try {
            result = await api.startFuncProcess(buildOutputPath, quoteFuncHostArguments(args), dcpEnv);
        } catch (error) {
            taskEndSubscription?.dispose();
            throw error;
        } finally {
            taskStartSubscription.dispose();
        }

        if (!result.success) {
            taskEndSubscription?.dispose();
            throw new Error(`Azure Functions extension failed to start func host: ${result.error ?? 'unknown error'}`);
        }

        const workerPid = result.processId;
        extensionLogOutputChannel.info(`Azure Functions worker process started (PID: ${workerPid})`);

        // Track the worker PID for cleanup
        const workerPidNumber = parseInt(workerPid, 10);
        workerPidsByRunId.set(debugConfiguration.runId, workerPidNumber);

        if (!launchOptions.debug) {
            const runId = debugConfiguration.runId;
            let completeSession: (exitCode: number) => void;
            const termination = new Promise<number>(resolve => {
                completeSession = resolve;
            });
            let completed = false;
            const complete = (exitCode: number): void => {
                if (completed) {
                    return;
                }

                completed = true;
                taskEndSubscription?.dispose();
                cleanupRun(runId);
                completeSession(exitCode);
            };
            completeFuncSession = complete;
            if (pendingFuncExitCode !== undefined) {
                complete(pendingFuncExitCode);
            }

            return {
                id: runId,
                processId: workerPidNumber,
                session: { id: runId } as vscode.DebugSession,
                stopSession: async () => {
                    complete(-1);
                },
                termination
            };
        }

        // Configure coreclr attach to the worker process
        debugConfiguration.type = 'coreclr';
        debugConfiguration.request = 'attach';
        debugConfiguration.processId = String(workerPidNumber);

        // Remove launch-mode properties that don't apply to attach
        delete debugConfiguration.program;
        delete debugConfiguration.args;
        delete debugConfiguration.cwd;
        delete debugConfiguration.console;
        delete debugConfiguration.env;
    }
};
