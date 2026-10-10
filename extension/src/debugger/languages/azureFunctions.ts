import * as fs from 'fs';
import http = require('http');
import https = require('https');
import * as path from 'path';
import * as vscode from 'vscode';
import { azureFunctionsExtensionId, csharpExtensionId } from '../../capabilities';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isAzureFunctionsLaunchConfiguration } from '../../dcp/types';
import {
    azureFunctionsCmdDelayedExpansion,
    azureFunctionsCmdPercentArgument,
    azureFunctionsHostStartupTimedOut,
    azureFunctionsInvalidProcessId,
    azureFunctionsTaskExitedBeforeStartup,
    azureFunctionsUnsupportedTaskShell,
    azureFunctionsWorkerStartupTimedOut,
    invalidLaunchConfiguration
} from '../../loc/strings';
import { assertNoTerminalControlCharacters, quoteShellArg } from '../../utils/AspireTerminalProvider';
import { quoteCmdArgument } from '../../utils/cmdShim';
import { extensionLogOutputChannel } from '../../utils/logging';
import { AlreadyStartedResourceDebugSession, ResourceDebuggerExtension } from '../debuggerExtensions';
import { DotNetService } from './dotnet';
import { cleanupRun, registerRunCleanup } from '../runCleanupRegistry';

const AF_EXTENSION_ID = azureFunctionsExtensionId;
const DEFAULT_PICK_PROCESS_TIMEOUT_SECONDS = 30;
const FUNC_HOST_DEFAULT_PORT = 7071;
const POLL_INTERVAL_MS = 100;
const REQUEST_TIMEOUT_MS = 1_000;
const TASK_SHUTDOWN_TIMEOUT_MS = 30_000;
const TEMP_DIRECTORY_CLEANUP_TIMEOUT_MS = 30_000;
const TEMP_DIRECTORY_CLEANUP_RETRY_DELAY_MS = 100;
// Node validates process IDs by signed 32-bit coercion before dispatching to the OS.
// Keep debugger attach within that same supported range.
const MAX_WORKER_PROCESS_ID = 0x7fffffff;
const TEMP_DIRECTORY_CLEANUP_MAX_ATTEMPTS =
    TEMP_DIRECTORY_CLEANUP_TIMEOUT_MS / TEMP_DIRECTORY_CLEANUP_RETRY_DELAY_MS;

type FuncHostTaskShell = 'cmd' | 'fish' | 'powershell' | 'posix';

type TerminalProfileConfiguration = {
    path?: string | string[];
    source?: string;
};

type WorkerProcessIdDiscovery = {
    jsonOutputFile: string;
    initialContents: string;
};

type FuncRunState = {
    runId: string;
    task?: vscode.Task;
    taskExecution?: vscode.TaskExecution;
    taskProcessId?: number;
    workerProcessId?: number;
    ownedTempDirectory?: string;
    taskExitCode?: number;
    taskLaunchStarted: boolean;
    taskExecutionReadyResolved: boolean;
    taskExecutionReady: Promise<vscode.TaskExecution | undefined>;
    resolveTaskExecutionReady: (execution: vscode.TaskExecution | undefined) => void;
    taskStarted: Promise<void>;
    resolveTaskStarted: () => void;
    taskExited: Promise<number>;
    resolveTaskExited: (exitCode: number) => void;
    taskStartSubscription?: vscode.Disposable;
    taskEndSubscription?: vscode.Disposable;
    startupAbortController: AbortController;
    startupTimeout?: NodeJS.Timeout;
    shutdownTimeout?: NodeJS.Timeout;
    tempDirectoryCleanupRetryTimer?: NodeJS.Timeout;
    tempDirectoryCleanup?: Promise<void>;
    cleanup?: Promise<void>;
    stop?: Promise<void>;
    terminateRequested: boolean;
    stopRequested: boolean;
    disposed: boolean;
};

function createFuncRunState(runId: string): FuncRunState {
    let resolveTaskExecutionReady!: (execution: vscode.TaskExecution | undefined) => void;
    const taskExecutionReady = new Promise<vscode.TaskExecution | undefined>(resolve => resolveTaskExecutionReady = resolve);
    let resolveTaskStarted!: () => void;
    const taskStarted = new Promise<void>(resolve => resolveTaskStarted = resolve);
    let resolveTaskExited!: (exitCode: number) => void;
    const taskExited = new Promise<number>(resolve => resolveTaskExited = resolve);

    return {
        runId,
        taskLaunchStarted: false,
        taskExecutionReadyResolved: false,
        taskExecutionReady,
        resolveTaskExecutionReady,
        taskStarted,
        resolveTaskStarted,
        taskExited,
        resolveTaskExited,
        startupAbortController: new AbortController(),
        terminateRequested: false,
        stopRequested: false,
        disposed: false
    };
}

function setTaskExecution(state: FuncRunState, execution: vscode.TaskExecution): void {
    state.taskExecution ??= execution;
    if (!state.taskExecutionReadyResolved) {
        state.taskExecutionReadyResolved = true;
        state.resolveTaskExecutionReady(state.taskExecution);
    }
}

function completeTaskExecutionDiscovery(state: FuncRunState): void {
    if (!state.taskExecutionReadyResolved) {
        state.taskExecutionReadyResolved = true;
        state.resolveTaskExecutionReady(undefined);
    }
}

function removeOwnedTempDirectory(state: FuncRunState): Promise<void> {
    if (state.tempDirectoryCleanup) {
        return state.tempDirectoryCleanup;
    }

    let resolveCleanup!: () => void;
    const cleanup = new Promise<void>(resolve => resolveCleanup = resolve);
    state.tempDirectoryCleanup = cleanup;
    const tempDirectory = state.ownedTempDirectory;
    if (!tempDirectory) {
        resolveCleanup();
        return cleanup;
    }

    let attempt = 1;
    const remove = (): void => {
        try {
            fs.rmSync(tempDirectory, { recursive: true, force: true });
            state.ownedTempDirectory = undefined;
            state.tempDirectoryCleanupRetryTimer = undefined;
            resolveCleanup();
        } catch (error) {
            if (attempt < TEMP_DIRECTORY_CLEANUP_MAX_ATTEMPTS) {
                attempt++;
                // TaskExecution.terminate() only requests termination. Keep retrying for
                // a bounded shutdown period so Windows can release Core Tools files.
                state.tempDirectoryCleanupRetryTimer = setTimeout(remove, TEMP_DIRECTORY_CLEANUP_RETRY_DELAY_MS);
                state.tempDirectoryCleanupRetryTimer.unref();
                return;
            }

            state.tempDirectoryCleanupRetryTimer = undefined;
            resolveCleanup();
        }
    };

    remove();
    return cleanup;
}

function finalizeOwnedTempDirectoryRemoval(state: FuncRunState): void {
    const tempDirectory = state.ownedTempDirectory;
    if (!tempDirectory) {
        return;
    }

    try {
        fs.rmSync(tempDirectory, { recursive: true, force: true });
    } catch (error) {
        extensionLogOutputChannel.warn(`Failed to remove Azure Functions temporary directory ${tempDirectory}: ${error}`);
    } finally {
        state.ownedTempDirectory = undefined;
    }
}

function disposeFuncRunState(state: FuncRunState): void {
    if (state.disposed) {
        return;
    }

    state.disposed = true;
    if (state.startupTimeout) {
        clearTimeout(state.startupTimeout);
        state.startupTimeout = undefined;
    }
    if (state.shutdownTimeout) {
        clearTimeout(state.shutdownTimeout);
        state.shutdownTimeout = undefined;
    }
    state.taskStartSubscription?.dispose();
    state.taskStartSubscription = undefined;
    state.taskEndSubscription?.dispose();
    state.taskEndSubscription = undefined;
}

function requestFuncTaskTermination(state: FuncRunState, taskExecution: vscode.TaskExecution): boolean {
    if (state.terminateRequested) {
        return true;
    }

    state.terminateRequested = true;
    extensionLogOutputChannel.info(`Terminating func host task for runId ${state.runId}`);
    try {
        taskExecution.terminate();
        return true;
    } catch (error) {
        extensionLogOutputChannel.warn(`Failed to terminate Azure Functions task for runId ${state.runId}: ${error}`);
        return false;
    }
}

async function terminateFuncTaskAndWaitForExit(state: FuncRunState): Promise<void> {
    let taskExecution = state.taskExecution;
    if (!taskExecution && state.taskLaunchStarted) {
        taskExecution = await state.taskExecutionReady;
    }
    if (!taskExecution || state.taskExitCode !== undefined) {
        return;
    }

    if (!requestFuncTaskTermination(state, taskExecution)) {
        return;
    }

    await new Promise<void>(resolve => {
        let settled = false;
        const finish = (): void => {
            if (settled) {
                return;
            }

            settled = true;
            if (state.shutdownTimeout) {
                clearTimeout(state.shutdownTimeout);
                state.shutdownTimeout = undefined;
            }
            resolve();
        };

        void state.taskExited.then(() => finish());
        state.shutdownTimeout = setTimeout(() => {
            extensionLogOutputChannel.warn(
                `Azure Functions task for runId ${state.runId} did not report an exit within ${TASK_SHUTDOWN_TIMEOUT_MS / 1_000} seconds after termination.`);
            finish();
        }, TASK_SHUTDOWN_TIMEOUT_MS);
        state.shutdownTimeout.unref();
    });
}

async function runFuncCleanup(state: FuncRunState): Promise<void> {
    state.startupAbortController.abort();
    await Promise.all([
        terminateFuncTaskAndWaitForExit(state),
        removeOwnedTempDirectory(state)
    ]);
    // The task can release its last Windows file lock at the same moment the
    // bounded retry window ends, so make one final attempt after task shutdown.
    finalizeOwnedTempDirectoryRemoval(state);
    disposeFuncRunState(state);
}

function cleanupFuncRun(state: FuncRunState): Promise<void> {
    if (state.cleanup) {
        return state.cleanup;
    }

    let resolveCleanup!: () => void;
    let rejectCleanup!: (error: unknown) => void;
    const cleanup = new Promise<void>((resolve, reject) => {
        resolveCleanup = resolve;
        rejectCleanup = reject;
    });
    state.cleanup = cleanup;
    void runFuncCleanup(state).then(resolveCleanup, rejectCleanup);

    return cleanup;
}

async function activateAzureFunctionsExtension(): Promise<void> {
    const extension = vscode.extensions.getExtension(AF_EXTENSION_ID);
    if (!extension) {
        throw new Error(`Azure Functions extension (${AF_EXTENSION_ID}) is not installed`);
    }

    // Activating the extension registers its `func` task definition and listeners.
    // Do not use startFuncProcess: vscode-azurefunctions 1.22.0 creates an unregistered
    // dynamic task type that VS Code 1.130 and later reject before Core Tools starts.
    await extension.activate();
}

function getPickProcessTimeoutSeconds(): number {
    const configuredTimeout = vscode.workspace.getConfiguration('azureFunctions').get<number>('pickProcessTimeout');
    return typeof configuredTimeout === 'number' && Number.isFinite(configuredTimeout) && configuredTimeout > 0
        ? configuredTimeout
        : DEFAULT_PICK_PROCESS_TIMEOUT_SECONDS;
}

function getFuncHostPort(args: string[]): number {
    for (let index = 0; index < args.length; index++) {
        const argument = args[index];
        if ((argument === '--port' || argument === '-p') && index + 1 < args.length) {
            const port = Number(args[index + 1]);
            if (Number.isInteger(port) && port > 0 && port <= 65_535) {
                return port;
            }
        }

        const match = /^(?:--port|-p)=(\d+)$/.exec(argument);
        if (match) {
            const port = Number(match[1]);
            if (port > 0 && port <= 65_535) {
                return port;
            }
        }
    }

    return FUNC_HOST_DEFAULT_PORT;
}

function createFuncTask(rawArgs: string[], quotedArgs: string[], buildOutputPath: string, env: Record<string, string>): vscode.Task {
    const commandLine = ['func', 'host', 'start', ...quotedArgs].join(' ');
    return new vscode.Task(
        { type: 'func', command: 'host start', args: rawArgs },
        vscode.TaskScope.Workspace,
        'func: host start',
        'func',
        new vscode.ShellExecution(commandLine, {
            cwd: buildOutputPath,
            env
        }));
}

function createTaskExitError(exitCode: number): Error {
    return new Error(azureFunctionsTaskExitedBeforeStartup(exitCode));
}

function throwIfAborted(signal: AbortSignal): void {
    if (signal.aborted) {
        throw signal.reason;
    }
}

function delay(milliseconds: number, signal: AbortSignal): Promise<void> {
    throwIfAborted(signal);

    return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
            signal.removeEventListener('abort', abort);
            resolve();
        }, milliseconds);
        const abort = (): void => {
            clearTimeout(timeout);
            signal.removeEventListener('abort', abort);
            reject(signal.reason);
        };
        signal.addEventListener('abort', abort, { once: true });
    });
}

async function probeFuncHostStatus(protocol: typeof http | typeof https, port: number, signal: AbortSignal): Promise<boolean> {
    throwIfAborted(signal);

    return await new Promise((resolve, reject) => {
        let settled = false;
        const finish = (result: boolean): void => {
            if (settled) {
                return;
            }

            settled = true;
            signal.removeEventListener('abort', abort);
            resolve(result);
        };
        const fail = (error: unknown): void => {
            if (settled) {
                return;
            }

            settled = true;
            signal.removeEventListener('abort', abort);
            reject(error);
        };
        const request = protocol.request({
            hostname: '127.0.0.1',
            port,
            path: '/admin/host/status',
            method: 'GET',
            rejectUnauthorized: false
        }, response => {
            if (response.statusCode !== 200) {
                response.resume();
                finish(false);
                return;
            }

            let body = '';
            response.setEncoding('utf8');
            response.on('data', chunk => body += chunk);
            response.on('end', () => {
                try {
                    const state = (JSON.parse(body) as { state?: unknown }).state;
                    finish(typeof state === 'string' && state.toLowerCase() === 'running');
                } catch {
                    finish(false);
                }
            });
        });
        const abort = (): void => {
            request.destroy();
            fail(signal.reason);
        };
        signal.addEventListener('abort', abort, { once: true });
        request.setTimeout(REQUEST_TIMEOUT_MS, () => {
            request.destroy();
            finish(false);
        });
        request.on('error', () => signal.aborted ? fail(signal.reason) : finish(false));
        request.end();
    });
}

async function waitForFuncHostRunning(port: number, deadline: number, timeoutSeconds: number, signal: AbortSignal): Promise<void> {
    while (Date.now() < deadline) {
        throwIfAborted(signal);
        if (await probeFuncHostStatus(http, port, signal) || await probeFuncHostStatus(https, port, signal)) {
            return;
        }

        await delay(POLL_INTERVAL_MS, signal);
    }

    throw new Error(azureFunctionsHostStartupTimedOut(timeoutSeconds, port));
}

function getJsonOutputFileArgument(args: string[]): string | undefined {
    for (let index = 0; index < args.length; index++) {
        const argument = args[index];
        if (argument === '--json-output-file') {
            return args[index + 1];
        }

        const match = /^--json-output-file=(.+)$/.exec(argument);
        if (match) {
            return match[1];
        }
    }

    return undefined;
}

function ensureFlagEnabled(args: string[], flag: string): void {
    let firstMatch = -1;
    for (let i = args.length - 1; i >= 0; i--) {
        const argument = args[i];
        if (argument === flag || argument.startsWith(`${flag}=`)) {
            const value = args[i + 1]?.toLowerCase();
            if (argument === flag && (value === 'true' || value === 'false')) {
                args.splice(i + 1, 1);
            }
            firstMatch = i;
            args.splice(i, 1);
        }
    }

    args.splice(firstMatch >= 0 ? firstMatch : args.length, 0, flag);
}

function readJsonOutputFile(jsonOutputFile: string): string | undefined {
    try {
        return fs.readFileSync(jsonOutputFile, 'utf8');
    } catch (error) {
        const code = (error as NodeJS.ErrnoException).code;
        if (code !== 'ENOENT') {
            extensionLogOutputChannel.warn(`Failed to read Azure Functions worker startup JSON: ${error}`);
        }
    }

    return undefined;
}

function readWorkerProcessId(discovery: WorkerProcessIdDiscovery): number | undefined {
    const contents = readJsonOutputFile(discovery.jsonOutputFile);
    if (contents === undefined) {
        return undefined;
    }

    // Core Tools appends newline-delimited JSON. For example:
    //   {"name":"dotnet-worker-startup","workerProcessId":4242}
    // Ignore content captured before this launch and use the latest valid worker event;
    // unrelated lines and a partially-written final line are expected while polling.
    const launchContents = contents.startsWith(discovery.initialContents)
        ? contents.slice(discovery.initialContents.length)
        : contents;
    let workerProcessId: number | undefined;
    for (const line of launchContents.split(/\r?\n/)) {
        if (!line) {
            continue;
        }

        let parsed: unknown;
        try {
            parsed = JSON.parse(line) as unknown;
        } catch {
            // The final NDJSON line may still be in flight.
            continue;
        }

        if (typeof parsed !== 'object' || parsed === null) {
            continue;
        }

        const event = parsed as { name?: unknown; workerProcessId?: unknown };
        if (event.name !== 'dotnet-worker-startup') {
            continue;
        }

        if (typeof event.workerProcessId !== 'number' ||
            !Number.isInteger(event.workerProcessId) ||
            event.workerProcessId <= 0 ||
            event.workerProcessId > MAX_WORKER_PROCESS_ID) {
            throw new Error(azureFunctionsInvalidProcessId(String(event.workerProcessId)));
        }

        workerProcessId = event.workerProcessId;
    }

    return workerProcessId;
}

async function waitForWorkerProcessId(discovery: WorkerProcessIdDiscovery, deadline: number, timeoutSeconds: number, signal: AbortSignal): Promise<number> {
    while (Date.now() < deadline) {
        throwIfAborted(signal);
        const workerProcessId = readWorkerProcessId(discovery);
        if (workerProcessId !== undefined) {
            return workerProcessId;
        }

        await delay(POLL_INTERVAL_MS, signal);
    }

    throw new Error(azureFunctionsWorkerStartupTimedOut(timeoutSeconds));
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
        /(?:^|[\\/\s])(ba|da|a|z|fi|k)?sh(?:\.exe)?(?:$|\s)/.test(identity)) {
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
    extensionId: csharpExtensionId,
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

        const rawArgs = [...(args ?? [])];
        // Validate caller-provided arguments before building the project. The final
        // argument list is quoted again after generated Core Tools flags are appended.
        quoteFuncHostArguments(rawArgs);

        const runId = debugConfiguration.runId;
        const projectPath = launchConfig.project_path;
        const dotNetService = new DotNetService(launchOptions.debugSession);
        // project_path from the hosting integration is currently a .csproj file path
        // (resolved by AzureFunctionsProjectMetadata.ResolveProjectPath). If Aspire
        // later supports non-.NET Functions resources, that launch config should carry
        // an explicit language/build contract instead of reusing this .NET project path.
        // Always build because path-based Functions resources do not have to be ProjectReferences
        // of the AppHost, so an existing target can be stale even after the AppHost was rebuilt.
        extensionLogOutputChannel.info(`Building Azure Functions project before starting func host: ${projectPath}`);
        await dotNetService.buildDotNetProject(projectPath);
        const targetPath = await dotNetService.getDotNetTargetPath(projectPath);
        const buildOutputPath = path.dirname(targetPath);
        extensionLogOutputChannel.info(`Starting Azure Functions project with a registered func task: ${projectPath} (buildPath: ${buildOutputPath})`);

        // ShellExecution inherits the VS Code process environment. Only add the
        // DCP-specific values so the task environment stays equivalent to the old path.
        const dcpEnv = Object.fromEntries(
            (env ?? []).filter(e => e.value !== undefined).map(e => [e.name, e.value])
        );
        await activateAzureFunctionsExtension();
        const state = createFuncRunState(runId);
        registerRunCleanup(runId, () => {
            void cleanupFuncRun(state);
        });
        const jsonOutputFileArgument = getJsonOutputFileArgument(rawArgs);
        let workerProcessIdDiscovery: WorkerProcessIdDiscovery;
        let ownedJsonOutputFileArgument: string | undefined;
        if (jsonOutputFileArgument) {
            const jsonOutputFile = path.resolve(buildOutputPath, jsonOutputFileArgument);
            workerProcessIdDiscovery = {
                jsonOutputFile,
                initialContents: readJsonOutputFile(jsonOutputFile) ?? ''
            };
        } else {
            const tempDirectory = fs.mkdtempSync(path.join(buildOutputPath, 'aspire-functions-worker-'));
            state.ownedTempDirectory = tempDirectory;
            const jsonOutputFile = path.join(tempDirectory, 'worker-startup.json');
            workerProcessIdDiscovery = { jsonOutputFile, initialContents: '' };
            ownedJsonOutputFileArgument = path.relative(buildOutputPath, jsonOutputFile).split(path.sep).join('/');
        }
        if (launchOptions.debug) {
            ensureFlagEnabled(rawArgs, '--dotnet-isolated-debug');
        }
        ensureFlagEnabled(rawArgs, '--enable-json-output');
        if (ownedJsonOutputFileArgument) {
            rawArgs.push('--json-output-file', ownedJsonOutputFileArgument);
        }

        let quotedArgs: string[];
        try {
            quotedArgs = quoteFuncHostArguments(rawArgs);
        } catch (error) {
            cleanupRun(runId);
            throw error;
        }

        state.task = createFuncTask(rawArgs, quotedArgs, buildOutputPath, dcpEnv);
        let completeSession: ((exitCode: number) => void) | undefined;
        let completed = false;
        const complete = (exitCode: number): void => {
            if (completed) {
                return;
            }

            completed = true;
            completeSession?.(exitCode);
        };

        // Register both process listeners before executeTask so a fast task start or
        // exit cannot race listener registration. Before executeTask resolves, task
        // object identity identifies this launch; afterward only its exact execution
        // is accepted.
        state.taskStartSubscription = vscode.tasks.onDidStartTaskProcess(event => {
            if (state.taskExecution && event.execution !== state.taskExecution) {
                return;
            }
            if (!state.taskExecution && event.execution.task !== state.task) {
                return;
            }

            setTaskExecution(state, event.execution);
            state.taskProcessId = event.processId;
            state.resolveTaskStarted();
        });
        state.taskEndSubscription = vscode.tasks.onDidEndTaskProcess(event => {
            if (state.taskExecution && event.execution !== state.taskExecution) {
                return;
            }
            if (!state.taskExecution && event.execution.task !== state.task) {
                return;
            }
            if (state.taskExitCode !== undefined) {
                return;
            }

            setTaskExecution(state, event.execution);
            state.taskExitCode = event.exitCode ?? 0;
            state.startupAbortController.abort(createTaskExitError(state.taskExitCode));
            state.resolveTaskExited(state.taskExitCode);
            cleanupRun(runId);
            if (!launchOptions.debug && completeSession && !state.stopRequested) {
                let normalizedExitCode = state.taskExitCode;
                // Exit code 143 is SIGTERM on macOS and Linux, matching the normal
                // debug-adapter termination path in adapterTracker.
                if ((process.platform === 'darwin' || process.platform === 'linux') && normalizedExitCode === 143) {
                    normalizedExitCode = 0;
                }
                complete(normalizedExitCode);
            }
        });

        try {
            const timeoutSeconds = getPickProcessTimeoutSeconds();
            const startupDeadline = Date.now() + timeoutSeconds * 1_000;
            const startupTimedOut = new Promise<never>((_, reject) => {
                state.startupTimeout = setTimeout(() => reject(new Error(launchOptions.debug
                    ? azureFunctionsWorkerStartupTimedOut(timeoutSeconds)
                    : azureFunctionsHostStartupTimedOut(timeoutSeconds, getFuncHostPort(rawArgs)))), timeoutSeconds * 1_000);
            });
            state.taskLaunchStarted = true;
            try {
                const taskExecution = vscode.tasks.executeTask(state.task).then(execution => {
                    setTaskExecution(state, execution);
                    if (state.startupAbortController.signal.aborted && state.taskExitCode === undefined) {
                        requestFuncTaskTermination(state, execution);
                    }
                    return execution;
                });
                await Promise.race([taskExecution, startupTimedOut]);
            } catch (error) {
                completeTaskExecutionDiscovery(state);
                throw error;
            }
            if (!state.taskProcessId) {
                await Promise.race([
                    state.taskStarted,
                    state.taskExited.then(exitCode => Promise.reject(createTaskExitError(exitCode))),
                    startupTimedOut
                ]);
            }

            const workerProcessId = waitForWorkerProcessId(
                workerProcessIdDiscovery,
                startupDeadline,
                timeoutSeconds,
                state.startupAbortController.signal);
            const readiness = launchOptions.debug
                ? workerProcessId
                : Promise.all([
                    waitForFuncHostRunning(
                        getFuncHostPort(rawArgs),
                        startupDeadline,
                        timeoutSeconds,
                        state.startupAbortController.signal),
                    workerProcessId
                ]).then(([, processId]) => processId);
            const processId = await Promise.race([
                readiness,
                state.taskExited.then(exitCode => Promise.reject(createTaskExitError(exitCode))),
                startupTimedOut
            ]);
            if (launchOptions.debug && state.taskExitCode !== undefined) {
                throw createTaskExitError(state.taskExitCode);
            }
            if (state.taskExitCode === undefined) {
                state.workerProcessId = processId;
            }
            extensionLogOutputChannel.info(`Azure Functions process started for runId ${runId} (PID: ${processId})`);

            if (!launchOptions.debug) {
                const termination = new Promise<number>(resolve => completeSession = resolve);
                if (state.taskExitCode !== undefined) {
                    let normalizedExitCode = state.taskExitCode;
                    if ((process.platform === 'darwin' || process.platform === 'linux') && normalizedExitCode === 143) {
                        normalizedExitCode = 0;
                    }
                    complete(normalizedExitCode);
                }

                return {
                    id: runId,
                    processId,
                    session: { id: runId } as vscode.DebugSession,
                    stopSession: () => {
                        if (!state.stop) {
                            state.stopRequested = true;
                            cleanupRun(runId);
                            state.stop = (state.cleanup ?? Promise.resolve()).then(() => complete(-1));
                        }

                        return state.stop;
                    },
                    termination
                };
            }

            debugConfiguration.type = 'coreclr';
            debugConfiguration.request = 'attach';
            debugConfiguration.processId = String(processId);

            delete debugConfiguration.program;
            delete debugConfiguration.args;
            delete debugConfiguration.cwd;
            delete debugConfiguration.console;
            delete debugConfiguration.env;
        } catch (error) {
            completeTaskExecutionDiscovery(state);
            cleanupRun(runId);
            if (state.taskExitCode !== undefined) {
                throw createTaskExitError(state.taskExitCode);
            }
            throw error;
        } finally {
            if (state.startupTimeout) {
                clearTimeout(state.startupTimeout);
                state.startupTimeout = undefined;
            }
            state.taskStartSubscription?.dispose();
            state.taskStartSubscription = undefined;
        }
    }
};
