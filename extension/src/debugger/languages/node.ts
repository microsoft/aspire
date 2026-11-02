import * as net from "net";
import * as path from "path";
import * as vscode from "vscode";
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, LaunchOptions, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { denoAppHostInspectorNotReady, denoAppHostRunCommandMissing, denoInspectorAddressUnavailable, nodeDisplayName, nodeLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { LaunchedChildProcess, LaunchedChildProcessQuery, SystemLaunchedChildProcessQuery, findDescendantProcessIds } from "../launchedChildProcessDiscovery";
import { getJavaScriptRuntimeDisplayName, getJavaScriptRuntimeTargetPath, jsRuntimeBaseFileTypes, launchMethodDirect, launchMethodPackageManager, resolveJavaScriptLaunchMethod } from "./javascriptRuntime";

function asNodeConfig(launchConfig: ExecutableLaunchConfiguration): JavaScriptRuntimeLaunchConfiguration {
    if (isJavaScriptRuntimeLaunchConfiguration(launchConfig) && launchConfig.type === 'node') {
        return launchConfig;
    }

    extensionLogOutputChannel.info(`The resource type was not node for ${JSON.stringify(launchConfig)}`);
    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

function isDenoRuntimeExecutable(runtimeExecutable: string | undefined): boolean {
    if (!runtimeExecutable) {
        return false;
    }

    const extension = path.extname(runtimeExecutable);
    return path.basename(runtimeExecutable, extension).toLowerCase() === 'deno';
}

/**
 * js-debug starts its attachSimplePort timeout before it spawns the runtime, and its 10-second default
 * can expire before a Deno AppHost opens its inspector on a slow machine, leaving Deno blocked in
 * --inspect-wait. Wait longer, but keep the wait bounded and separate from the CLI's debug startup
 * budget: that budget covers breakpoints after the debugger attaches, not an inspector that never opens.
 */
export const denoAppHostInspectorAttachTimeoutMs = 120_000;

// An explicitly configured debugger timeout (for example from the `debuggers` launch configuration
// overrides) is the user's choice and is preserved.
export function getAppHostInspectorAttachTimeoutMs(configuredTimeout: unknown): number {
    return typeof configuredTimeout === 'number' ? configuredTimeout : denoAppHostInspectorAttachTimeoutMs;
}

// Bounds the process lookup, which runs PowerShell CIM queries on Windows.
const inspectorWaitingProcessQueryTimeoutMs = 30_000;

export function hasCommandLineArgument(candidate: LaunchedChildProcess, argument: string): boolean {
    if (candidate.commandLineArguments) {
        return candidate.commandLineArguments.includes(argument);
    }

    // Windows reports a single command line string, e.g. `"C:\deno.exe" run --inspect-wait=127.0.0.1:1234 apphost.mts`.
    const index = candidate.command.indexOf(argument);
    const end = index + argument.length;
    return index >= 0 &&
        (index === 0 || /[\s"]/.test(candidate.command[index - 1])) &&
        (end === candidate.command.length || /[\s"]/.test(candidate.command[end]));
}

/**
 * Finds the extension host's descendants that were started with the given inspector argument. The runtime is
 * already running when this is called, so a single listing is enough. Every match is returned because a
 * Windows .cmd shim (for example an npm-installed deno.cmd) leaves both cmd.exe and deno.exe with the argument.
 */
export async function findInspectorWaitingProcessIds(
    inspectorArgument: string,
    query: LaunchedChildProcessQuery,
    launcherPid: number = process.pid): Promise<number[]> {
    const processes = await query.listProcesses(undefined, inspectorWaitingProcessQueryTimeoutMs);
    const matches: number[] = [];
    for (const pid of findDescendantProcessIds(launcherPid, false, processes) ?? []) {
        const listed = processes.find(candidate => candidate.pid === pid);
        const candidate = query.canTrustListedProcessIdentity === true || !query.getProcess
            ? listed
            : await query.getProcess(pid, undefined, inspectorWaitingProcessQueryTimeoutMs).catch(() => undefined);
        if (candidate && hasCommandLineArgument(candidate, inspectorArgument)) {
            matches.push(pid);
        }
    }

    return matches;
}

/**
 * js-debug only takes ownership of the runtime after it attaches to the inspector. If the inspector never
 * becomes ready (the attach times out) or the session is stopped while js-debug is still waiting, js-debug
 * fails the launch but leaves the runtime blocked in --inspect-wait. Stop that runtime, which is identified
 * by its unique inspector argument among the extension host's descendants.
 */
export function registerDenoAppHostInspectorCleanup(
    inspectorArgument: string,
    inspectorAddress: string,
    timeoutMs: number,
    launchOptions: LaunchOptions,
    query: LaunchedChildProcessQuery = new SystemLaunchedChildProcessQuery()): void {
    const isRootSession = (session: vscode.DebugSession) =>
        session.configuration.runId === launchOptions.runId &&
        session.configuration.debugSessionId === launchOptions.debugSessionId &&
        session.configuration.__pendingTargetId === undefined;

    let launchResponse: { success: boolean } | undefined;
    let sessionStarted = false;
    let cleanedUp = false;
    const disposables: vscode.Disposable[] = [];
    const dispose = () => disposables.splice(0).forEach(disposable => disposable.dispose());

    const stopUnattachedRuntime = () => {
        dispose();
        if (cleanedUp || !sessionStarted || launchResponse?.success === true) {
            return;
        }

        cleanedUp = true;
        if (launchResponse) {
            extensionLogOutputChannel.error(denoAppHostInspectorNotReady(inspectorAddress, Math.round(timeoutMs / 1000)));
        }
        else {
            extensionLogOutputChannel.info(`Stopping the Deno AppHost that was still waiting for a debugger at ${inspectorAddress}.`);
        }

        void findInspectorWaitingProcessIds(inspectorArgument, query).then(pids => {
            if (pids.length === 0) {
                extensionLogOutputChannel.info(`No Deno AppHost process waiting for a debugger at ${inspectorAddress} remained to stop.`);
            }

            for (const pid of pids) {
                try {
                    process.kill(pid);
                    extensionLogOutputChannel.info(`Stopped Deno AppHost process ${pid} that never attached to the debugger.`);
                }
                catch (error) {
                    extensionLogOutputChannel.warn(`Failed to stop Deno AppHost process ${pid}: ${error}`);
                }
            }
        }, error => {
            extensionLogOutputChannel.warn(`Failed to find the Deno AppHost process waiting for a debugger at ${inspectorAddress}: ${error}`);
        });
    };

    // Use the adapter's own messages rather than onDidTerminateDebugSession: VS Code does not report a
    // session whose launch request failed as terminated.
    disposables.push(vscode.debug.registerDebugAdapterTrackerFactory('pwa-node', {
        createDebugAdapterTracker: session => {
            if (!isRootSession(session)) {
                return undefined;
            }

            sessionStarted = true;
            return {
                onDidSendMessage: (message: { type?: string; command?: string; success?: boolean }) => {
                    if (message.type === 'response' && message.command === 'launch') {
                        launchResponse = { success: message.success === true };
                        if (launchResponse.success) {
                            dispose();
                        }
                        else {
                            stopUnattachedRuntime();
                        }
                    }
                },
                onWillStopSession: stopUnattachedRuntime,
            };
        }
    }));
    launchOptions.debugSession.registerResourceCleanup({ dispose: stopUnattachedRuntime });
}

async function getAvailableLoopbackPort(): Promise<number> {
    const server = net.createServer();

    return await new Promise<number>((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            const address = server.address();
            if (!address || typeof address === 'string') {
                server.close();
                reject(new Error(denoInspectorAddressUnavailable));
                return;
            }

            server.close(error => error ? reject(error) : resolve(address.port));
        });
    });
}

export const nodeDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'node',
    // Use js-debug's pwa-node adapter so outputCapture emits stdout/stderr DAP output events for dashboard log forwarding.
    debugAdapter: 'pwa-node',
    extensionId: null,
    getDisplayName: (launchConfig) => getJavaScriptRuntimeDisplayName(launchConfig, 'node', nodeDisplayName, nodeLabel),
    getSupportedFileTypes: () => jsRuntimeBaseFileTypes,
    getProjectFile: (launchConfig) => getJavaScriptRuntimeTargetPath(asNodeConfig(launchConfig)),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        const config = asNodeConfig(launchConfig);
        debugConfiguration.type = 'pwa-node';
        debugConfiguration.outputCapture = 'std';

        // Use working_directory for cwd if available
        if (config.working_directory) {
            debugConfiguration.cwd = config.working_directory;
        }

        if (config.runtime_executable) {
            debugConfiguration.runtimeExecutable = config.runtime_executable;
        }

        // For package manager script execution (e.g., npm run dev), use args directly as runtimeArgs.
        // The args from DCP already contain the full command (e.g., ["run", "dev", "--port", "5173"]).
        const launchMethod = resolveJavaScriptLaunchMethod(config, () => config.runtime_executable && config.runtime_executable !== 'node' ? launchMethodPackageManager : launchMethodDirect);
        if (launchMethod === launchMethodPackageManager) {
            const runtimeArgs = [...(args ?? [])];

            if (launchOptions.debug && launchOptions.isApphost && isDenoRuntimeExecutable(config.runtime_executable)) {
                const runArgumentIndex = runtimeArgs.indexOf('run');
                if (runArgumentIndex < 0) {
                    throw new Error(denoAppHostRunCommandMissing);
                }

                // Deno does not load js-debug's Node bootloader. Open its native inspector and tell
                // js-debug to attach directly, matching the official Deno VS Code provider.
                const inspectorPort = await getAvailableLoopbackPort();
                const inspectorArgument = `--inspect-wait=127.0.0.1:${inspectorPort}`;
                runtimeArgs.splice(runArgumentIndex + 1, 0, inspectorArgument);
                debugConfiguration.attachSimplePort = inspectorPort;
                debugConfiguration.timeout = getAppHostInspectorAttachTimeoutMs(debugConfiguration.timeout);
                registerDenoAppHostInspectorCleanup(inspectorArgument, `127.0.0.1:${inspectorPort}`, debugConfiguration.timeout, launchOptions);
            }

            debugConfiguration.runtimeArgs = runtimeArgs;
            delete debugConfiguration.args;
            delete debugConfiguration.program;
        }

        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
    }
};
