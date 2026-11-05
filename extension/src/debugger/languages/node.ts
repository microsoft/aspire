import * as net from "net";
import * as path from "path";
import * as vscode from "vscode";
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, LaunchOptions, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { denoAppHostInspectorNotReady, denoAppHostRunCommandMissing, denoInspectorAddressUnavailable, nodeDisplayName, nodeLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { LaunchedChildProcess, LaunchedChildProcessClock, LaunchedChildProcessQuery, SystemLaunchedChildProcessQuery, findDescendantProcessIds, systemLaunchedChildProcessClock } from "../launchedChildProcessDiscovery";
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

/**
 * A Deno AppHost always runs in js-debug's internal console. With `integratedTerminal` or `externalTerminal`
 * (for example from the `debuggers.apphost.console` override), VS Code's terminal service starts the runtime
 * outside the extension host's process tree. js-debug only owns the runtime once it attaches, so if the
 * inspector attach times out or is cancelled, inspector cleanup could not find it and Deno would stay blocked
 * in --inspect-wait. A terminal would also bypass `outputCapture`, which forwards the AppHost's output.
 */
export function getDenoAppHostDebugConsole(configuredConsole: unknown): 'internalConsole' {
    if (configuredConsole !== undefined && configuredConsole !== 'internalConsole') {
        extensionLogOutputChannel.warn(`Ignoring console '${configuredConsole}' for the Deno AppHost: it is always debugged in the internal console so a runtime that never attaches can be stopped.`);
    }

    return 'internalConsole';
}

// Bounds the process lookup, which runs PowerShell CIM queries on Windows.
const inspectorWaitingProcessQueryTimeoutMs = 30_000;

// How long cleanup keeps looking for the runtime. js-debug may still be spawning it when the session is
// cancelled, so a single listing can miss it; the window also bounds confirming that it exited.
export const inspectorWaitingProcessSettleTimeoutMs = 15_000;
const inspectorWaitingProcessInitialRetryDelayMs = 100;
const inspectorWaitingProcessMaximumRetryDelayMs = 1_000;

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

// Descendants of processes that may already have exited: a child keeps its exited parent's pid as parentPid.
function findDescendantsOfKnownProcesses(rootPids: readonly number[], processes: readonly LaunchedChildProcess[]): number[] {
    const childrenByParentId = new Map<number, number[]>();
    for (const candidate of processes) {
        const children = childrenByParentId.get(candidate.parentPid) ?? [];
        children.push(candidate.pid);
        childrenByParentId.set(candidate.parentPid, children);
    }

    const visited = new Set(rootPids);
    const pending = [...rootPids];
    const descendants: number[] = [];
    while (pending.length > 0) {
        for (const child of childrenByParentId.get(pending.pop()!) ?? []) {
            if (!visited.has(child)) {
                visited.add(child);
                descendants.push(child);
                pending.push(child);
            }
        }
    }

    return descendants;
}

// A process-detail lookup that fails because the process no longer exists (for example a missing /proc entry).
function isProcessGoneError(error: unknown): boolean {
    const code = (error as NodeJS.ErrnoException | undefined)?.code;
    return code === 'ENOENT' || code === 'ESRCH';
}

export interface InspectorWaitingProcessIds {
    /** Listed processes confirmed to have been started with the inspector argument. */
    readonly matches: readonly number[];
    /** Listed processes whose command line could not be read, so whether they match is unknown. */
    readonly unidentified: readonly number[];
}

/**
 * Finds the extension host's descendants that were started with the given inspector argument. Every match is
 * returned because a Windows .cmd shim (for example an npm-installed deno.cmd) leaves both cmd.exe and deno.exe with the argument.
 *
 * Stopping the shim detaches deno.exe from the extension host's process tree, so processes matched earlier are
 * passed as `knownProcessIds`: they are matched again while still listed, and their descendants are searched
 * even after they have exited.
 *
 * POSIX listings only carry pids and parent pids, so each candidate's command line is read separately. A failed
 * read (for example a timeout or I/O error) does not show the process exited, so it is reported as unidentified
 * rather than as a non-match, unless a second listing shows the process has exited. Only that, or a lookup that
 * finds the process gone, confirms it is not running.
 */
export async function findInspectorWaitingProcessIds(
    inspectorArgument: string,
    query: LaunchedChildProcessQuery,
    launcherPid: number = process.pid,
    knownProcessIds: readonly number[] = []): Promise<InspectorWaitingProcessIds> {
    const processes = await query.listProcesses(undefined, inspectorWaitingProcessQueryTimeoutMs);
    const candidatePids = new Set<number>(findDescendantProcessIds(launcherPid, false, processes) ?? []);
    for (const pid of knownProcessIds) {
        if (processes.some(candidate => candidate.pid === pid)) {
            candidatePids.add(pid);
        }
    }

    for (const pid of findDescendantsOfKnownProcesses(knownProcessIds, processes)) {
        candidatePids.add(pid);
    }

    const matches: number[] = [];
    const unidentified: number[] = [];
    for (const pid of candidatePids) {
        let candidate: LaunchedChildProcess | undefined;
        if (query.canTrustListedProcessIdentity === true || !query.getProcess) {
            candidate = processes.find(listed => listed.pid === pid);
        }
        else {
            try {
                candidate = await query.getProcess(pid, undefined, inspectorWaitingProcessQueryTimeoutMs);
            }
            catch (error) {
                if (!isProcessGoneError(error)) {
                    unidentified.push(pid);
                }

                continue;
            }
        }

        if (candidate && hasCommandLineArgument(candidate, inspectorArgument)) {
            matches.push(pid);
        }
    }

    if (unidentified.length > 0) {
        // A lookup can also fail because the process exited after the listing, as with the `ps` helper that
        // produced the listing itself, whose `ps -p` lookup fails with a generic error on macOS. Only processes
        // a later listing still shows remain unidentified; if that listing fails, nothing is confirmed gone.
        try {
            const stillListed = new Set((await query.listProcesses(undefined, inspectorWaitingProcessQueryTimeoutMs)).map(listed => listed.pid));
            return { matches, unidentified: unidentified.filter(pid => stillListed.has(pid)) };
        }
        catch {
            return { matches, unidentified };
        }
    }

    return { matches, unidentified };
}

export interface InspectorWaitingProcessStopResult {
    /** Processes that were signalled and then confirmed gone from the process listing. */
    readonly stopped: readonly number[];
    /** Processes still listed when the settle window ended. */
    readonly running: readonly number[];
    /** Processes still listed when the settle window ended whose identity could not be read, so they may be the runtime. */
    readonly unidentified: readonly number[];
}

/**
 * Stops every extension host descendant started with the inspector argument and confirms each one exited.
 * It keeps polling (with backoff, bounded by the settle window) until it has stopped at least one process and
 * a later listing no longer shows any, so a runtime that js-debug spawns just after cancellation is still
 * found and a stop is only reported once it is observed. Each listing also checks the processes already
 * signalled and their descendants, so a deno.exe left behind by a stopped deno.cmd shim is still found.
 * Once `isSpawnSettled` reports that the runtime can no longer be spawned, an empty listing ends the search.
 * A failed listing is never treated as empty: known processes are reported as still running, and if none
 * are known when the settle window ends, the listing error is thrown. Likewise a listed process whose identity
 * cannot be read keeps the search going and is never signalled; it is only confirmed gone once a later listing
 * no longer shows it, and is otherwise reported as unidentified.
 */
export async function stopInspectorWaitingProcesses(
    inspectorArgument: string,
    query: LaunchedChildProcessQuery,
    clock: LaunchedChildProcessClock = systemLaunchedChildProcessClock,
    options: {
        readonly launcherPid?: number;
        readonly settleTimeoutMs?: number;
        readonly kill?: (pid: number) => void;
        /** Processes an earlier attempt signalled but could not confirm exited. */
        readonly knownProcessIds?: readonly number[];
        /** Returns true once nothing can spawn the runtime any more, so an empty listing means there is none. */
        readonly isSpawnSettled?: () => boolean;
    } = {},
): Promise<InspectorWaitingProcessStopResult> {
    const kill = options.kill ?? ((pid: number) => process.kill(pid));
    const knownProcessIds = options.knownProcessIds ?? [];
    const deadline = clock.now() + (options.settleTimeoutMs ?? inspectorWaitingProcessSettleTimeoutMs);
    const signalled = new Set<number>();
    let running: number[] = [];
    let unidentified: readonly number[] = [];
    let listed = false;
    let listingError: unknown;
    let retryDelayMs = inspectorWaitingProcessInitialRetryDelayMs;

    while (true) {
        // Sampled before listing: a runtime spawned while the lookups run is missing from this listing even if
        // the launch has finished by the time they return, so only an earlier settle makes an empty result final.
        const spawnSettled = options.isSpawnSettled?.() === true;
        try {
            // Unidentified processes are followed too: stopping a POSIX wrapper can reparent its runtime outside
            // the extension host's tree. Like every known process, they are only signalled once a listing matches them.
            const found = await findInspectorWaitingProcessIds(inspectorArgument, query, options.launcherPid ?? process.pid, [...knownProcessIds, ...signalled, ...unidentified]);
            running = [...found.matches];
            unidentified = found.unidentified;
            listed = true;
        }
        catch (error) {
            // Without a listing nothing can be confirmed: processes known from an earlier attempt or already
            // signalled stay unconfirmed, and a failed listing never counts as proof that nothing is running.
            // Processes the previous listing could not identify stay unidentified.
            listed = false;
            listingError = error;
            running = [...new Set([...knownProcessIds, ...signalled])];
        }

        if (listed && running.length === 0 && unidentified.length === 0 && (signalled.size > 0 || spawnSettled)) {
            break;
        }

        // Only signal processes a successful listing matched: a PID from a failed lookup or an earlier attempt
        // may have exited and been reused by an unrelated process.
        for (const pid of listed ? running : []) {
            if (!signalled.has(pid)) {
                signalled.add(pid);
                try {
                    kill(pid);
                }
                catch (error) {
                    extensionLogOutputChannel.warn(`Failed to stop Deno AppHost process ${pid}: ${error}`);
                }
            }
        }

        const remainingMs = deadline - clock.now();
        if (remainingMs <= 0) {
            break;
        }

        await clock.sleep(Math.min(retryDelayMs, remainingMs));
        retryDelayMs = Math.min(inspectorWaitingProcessMaximumRetryDelayMs, retryDelayMs * 2);
    }

    unidentified = unidentified.filter(pid => !running.includes(pid));
    if (!listed && running.length === 0 && unidentified.length === 0) {
        // Nothing was identified and the last listing failed, so it is unknown whether a runtime is running.
        throw listingError;
    }

    return { stopped: [...signalled].filter(pid => !running.includes(pid) && !unidentified.includes(pid)), running, unidentified };
}

/**
 * js-debug's launch error for an attachSimplePort timeout does not say it timed out, and a launch can also fail
 * for unrelated reasons (for example a missing executable). Only call it an inspector timeout when the launch
 * request ran for the whole configured timeout, and always keep js-debug's own reason.
 */
export function getDenoAppHostLaunchFailureMessage(
    inspectorAddress: string,
    timeoutMs: number,
    elapsedMs: number,
    reason: string | undefined): string {
    const detail = reason ?? 'unknown error';
    return elapsedMs >= timeoutMs
        ? denoAppHostInspectorNotReady(inspectorAddress, Math.round(timeoutMs / 1000), detail)
        : `The debugger could not launch the Deno AppHost: ${detail}`;
}

/**
 * js-debug only takes ownership of the runtime after it attaches to the inspector. If the inspector never
 * becomes ready (the attach times out) or the session is stopped while js-debug is still waiting, js-debug
 * fails the launch but leaves the runtime blocked in --inspect-wait. Stop that runtime, which is identified
 * by its unique inspector argument among the extension host's descendants.
 *
 * The pending launch also holds up the owning Aspire session's shutdown, which waits for pending resource
 * starts, so register a pending-start cancellation that stops the js-debug session until it has attached.
 */
export function registerDenoAppHostInspectorCleanup(
    inspectorArgument: string,
    inspectorAddress: string,
    timeoutMs: number,
    launchOptions: LaunchOptions,
    query: LaunchedChildProcessQuery = new SystemLaunchedChildProcessQuery(),
    clock: LaunchedChildProcessClock = systemLaunchedChildProcessClock): void {
    const isRootSession = (session: vscode.DebugSession) =>
        session.configuration.runId === launchOptions.runId &&
        session.configuration.debugSessionId === launchOptions.debugSessionId &&
        session.configuration.__pendingTargetId === undefined;

    let rootSession: vscode.DebugSession | undefined;
    let launchRequestedAt: number | undefined;
    let launchSucceeded = false;
    // js-debug spawns the runtime while it handles the launch request, so once it has answered (or the
    // adapter has exited) a runtime that is not listed was never started.
    let launchFinished = false;
    let unconfirmedProcessIds: readonly number[] = [];
    let pendingStartCancelled = false;
    let cleanedUp = false;
    let pendingStartCancellation: vscode.Disposable | undefined;
    let trackerFactory: vscode.Disposable | undefined;
    // The tracker stays registered after cleanup starts: cleanup still needs to see the launch response.
    const dispose = () => {
        pendingStartCancellation?.dispose();
        trackerFactory?.dispose();
    };

    const stopRootSession = (session: vscode.DebugSession) => {
        Promise.resolve(vscode.debug.stopDebugging(session)).catch(error => {
            extensionLogOutputChannel.warn(`Failed to stop the pending Deno AppHost debug session: ${error}`);
        });
    };

    const stopUnattachedRuntime = () => {
        pendingStartCancellation?.dispose();
        if (!rootSession) {
            trackerFactory?.dispose();
        }

        if (cleanedUp || !rootSession || launchSucceeded) {
            return;
        }

        cleanedUp = true;
        extensionLogOutputChannel.info(`Stopping the Deno AppHost that was waiting for a debugger at ${inspectorAddress}.`);
        const runCleanup = () => {
            const cleanup = stopInspectorWaitingProcesses(inspectorArgument, query, clock, {
                knownProcessIds: unconfirmedProcessIds,
                isSpawnSettled: () => launchFinished,
            }).then(({ stopped, running, unidentified }) => {
                // A retry matches these again before signalling any of them, so a reused PID is never stopped.
                unconfirmedProcessIds = [...running, ...unidentified];
                if (stopped.length > 0) {
                    extensionLogOutputChannel.info(`Stopped Deno AppHost process ${stopped.join(', ')} that never attached to the debugger.`);
                }

                if (running.length > 0) {
                    throw new Error(`Deno AppHost process ${running.join(', ')} waiting for a debugger at ${inspectorAddress} was still running after cleanup.`);
                }

                if (unidentified.length > 0) {
                    throw new Error(`Could not read the command line of process ${unidentified.join(', ')} to confirm whether it is the Deno AppHost waiting for a debugger at ${inspectorAddress}.`);
                }

                if (stopped.length === 0) {
                    extensionLogOutputChannel.info(`No Deno AppHost process waiting for a debugger at ${inspectorAddress} was found.`);
                }
            });
            void cleanup.catch(error => {
                extensionLogOutputChannel.warn(`Failed to stop the Deno AppHost waiting for a debugger at ${inspectorAddress}: ${error instanceof Error ? error.message : error}`);
            });
            return cleanup;
        };
        // The owning session's shutdown waits for the runtime to exit, so a workspace deletion or restart
        // that follows it does not race the still-running process. A failed cleanup runs again on the next shutdown.
        launchOptions.debugSession.trackPendingResourceCleanup(rootSession.name, runCleanup);
    };

    pendingStartCancellation = launchOptions.debugSession.registerPendingStartCancellation({
        dispose: () => {
            pendingStartCancelled = true;
            if (rootSession) {
                stopRootSession(rootSession);
            }
        }
    });

    // Use the adapter's own messages rather than onDidTerminateDebugSession: VS Code does not report a
    // session whose launch request failed as terminated.
    trackerFactory = vscode.debug.registerDebugAdapterTrackerFactory('pwa-node', {
        createDebugAdapterTracker: session => {
            if (rootSession || !isRootSession(session)) {
                return undefined;
            }

            rootSession = session;
            return {
                onWillStartSession: () => {
                    // The owning session stopped before this session was created.
                    if (pendingStartCancelled) {
                        stopRootSession(session);
                    }
                },
                onWillReceiveMessage: (message: { type?: string; command?: string }) => {
                    if (message.type === 'request' && message.command === 'launch') {
                        launchRequestedAt = clock.now();
                    }
                },
                onDidSendMessage: (message: { type?: string; command?: string; success?: boolean; message?: string; body?: { error?: { format?: string } } }) => {
                    if (message.type !== 'response' || message.command !== 'launch') {
                        return;
                    }

                    launchFinished = true;
                    if (message.success === true) {
                        launchSucceeded = true;
                        dispose();
                        return;
                    }

                    if (!pendingStartCancelled && !cleanedUp) {
                        extensionLogOutputChannel.error(getDenoAppHostLaunchFailureMessage(
                            inspectorAddress,
                            timeoutMs,
                            clock.now() - (launchRequestedAt ?? clock.now()),
                            message.body?.error?.format ?? message.message));
                    }

                    stopUnattachedRuntime();
                },
                onWillStopSession: stopUnattachedRuntime,
                onExit: () => {
                    launchFinished = true;
                    dispose();
                },
            };
        }
    });
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
                debugConfiguration.console = getDenoAppHostDebugConsole(debugConfiguration.console);
                registerDenoAppHostInspectorCleanup(inspectorArgument, `127.0.0.1:${inspectorPort}`, debugConfiguration.timeout, launchOptions);
            }

            debugConfiguration.runtimeArgs = runtimeArgs;
            delete debugConfiguration.args;
            delete debugConfiguration.program;
        }

        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
    }
};
