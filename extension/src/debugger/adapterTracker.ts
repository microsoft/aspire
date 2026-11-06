import * as vscode from 'vscode';
import { ServiceLogsNotification, ProcessRestartedNotification, SessionTerminatedNotification, AspireResourceExtendedDebugConfiguration } from "../dcp/types";
import { extensionLogOutputChannel } from "../utils/logging";
import AspireDcpServer from '../dcp/AspireDcpServer';
import { removeTrailingNewline } from '../utils/strings';
import { dcpServerNotInitialized } from '../loc/strings';

/**
 * Callback invoked when a restart is requested on an app host debug session.
 * Return `true` to clear the `restart` flag of the outgoing disconnect or terminate request, so the
 * adapter shuts down completely. That does not cancel VS Code's relaunch of the terminated child,
 * which the handler has to stop itself.
 */
export type AppHostRestartHandler = (debugSessionId: string) => boolean;
export type AppHostTerminationRequestHandler = (debugSessionId: string) => void;

/**
 * DAP output event categories. Per the DAP spec the `category` field is optional;
 * when missing, clients should treat it as `'console'`. This union keeps the known
 * categories explicit while allowing adapter-specific values via the `(string & {})`
 * trick, and includes `undefined` so callers can't accidentally rely on it being set.
 */
export type DapOutputCategory = 'console' | 'important' | 'stdout' | 'stderr' | 'debug' | 'telemetry' | (string & {}) | undefined;
export type AppHostOutputHandler = (output: string, category: DapOutputCategory) => void;
export type DebuggeeProcessHandler = (session: vscode.DebugSession, processId: number | undefined) => void;

export interface AppHostTrackerOptions {
    // VS Code invokes every factory registered for an adapter type for every matching
    // debug session. Scope AppHost output to the synthetic Aspire session that
    // registered this tracker so concurrent AppHosts cannot consume each other's logs.
    debugSessionId: string;
    onRestartRequested?: AppHostRestartHandler;
    onOutput?: AppHostOutputHandler;
    onTerminationRequested?: AppHostTerminationRequestHandler;
    // Hides the adapter's `supportsRestartRequest` capability from VS Code for the AppHost session, so
    // the toolbar restart arrives as disconnect with `restart: true` and reaches onRestartRequested.
    disableDirectRestart?: boolean;
}

export function createDebugAdapterTracker(
    dcpServer: AspireDcpServer,
    debugAdapter: string,
    appHostTracker?: AppHostTrackerOptions,
    onDebuggeeProcess?: DebuggeeProcessHandler,
): vscode.Disposable {
    return vscode.debug.registerDebugAdapterTrackerFactory(debugAdapter, {
        createDebugAdapterTracker(session: vscode.DebugSession) {
            const configuration = session.configuration;
            if (!isDebugConfigurationWithId(configuration) || configuration.debugSessionId === null) {
                return undefined;
            }
            const debugSessionId = configuration.debugSessionId;
            const isOwnedAppHostSession = configuration.isApphost && appHostTracker?.debugSessionId === debugSessionId;
            if (configuration.isApphost && !isOwnedAppHostSession) {
                return undefined;
            }
            // The AppHost child is tracked for output and restart handling, but it is not
            // a DCP resource run. Its run ID is intentionally empty, and forwarding its
            // lifecycle would make DCP reject the notification and recycle the shared socket.
            const hasDcpRunSession = configuration.isApphost !== true;

            let debuggeeExitCode: number | undefined;
            let appHostExitObserved = false;

            return {
                onWillReceiveMessage: message => {
                    if (configuration.isApphost
                        && isOwnedAppHostSession
                        && (message.command === 'disconnect' || message.command === 'terminate')
                        && !appHostExitObserved
                        && debugSessionId) {
                        if (message.arguments?.restart) {
                            const shouldSuppress = appHostTracker?.onRestartRequested?.(debugSessionId) ?? false;
                            if (shouldSuppress) {
                                message.arguments.restart = false;
                            }
                        }
                        else if (message.command === 'terminate' ||
                            (message.command === 'disconnect' && message.arguments?.terminateDebuggee === true)) {
                            // VS Code can send disconnect({ terminateDebuggee: false }) only to
                            // clean up an adapter. Treat only explicit debuggee termination as user
                            // intent so a pre-start crash still records its launch failure.
                            appHostTracker?.onTerminationRequested?.(debugSessionId);
                        }
                    }
                },
                onDidSendMessage: message => {
                    if (isOwnedAppHostSession
                        && appHostTracker?.disableDirectRestart
                        && message.type === 'response'
                        && message.command === 'initialize'
                        && message.body) {
                        // Delve advertises supportsRestartRequest. With that capability VS Code restarts
                        // the session with a DAP restart request instead of disconnect with
                        // restart: true, and only the disconnect reaches the ordered restart path in
                        // onWillReceiveMessage. The extension host passes this same message object on
                        // to VS Code after the tracker runs, so clearing the capability here makes the
                        // toolbar restart take the disconnect route. VS Code then terminates the child and
                        // relaunches it itself, which AspireDebugSession stops while it restarts the
                        // whole Aspire session.
                        // https://github.com/go-delve/delve/blob/master/service/dap/server.go
                        // https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/debug/browser/debugService.ts
                        message.body.supportsRestartRequest = false;
                    }

                    if (message.type === 'event' && message.event === 'process') {
                        // A new debuggee process invalidates exit state captured from a prior run.
                        // Reset before the DCP-session guard: AppHost child sessions do not have a
                        // DCP run session, but a restarted child must accept later user termination.
                        debuggeeExitCode = undefined;
                        appHostExitObserved = false;
                    }

                    if (configuration.isApphost &&
                        message.type === 'event' &&
                        (message.event === 'terminated' || message.event === 'exited')) {
                        // After a natural exit or crash, VS Code cleans up by sending
                        // disconnect({ terminateDebuggee: false }). The adapter has already
                        // reported the outcome, so that later request is not user intent.
                        appHostExitObserved = true;
                    }

                    if (message.type === 'event' && message.event === 'output') {
                        const { category, output } = message.body;
                        if (typeof output === 'string' && category !== 'telemetry') {
                            if (configuration.isApphost) {
                                if (isOwnedAppHostSession) {
                                    // Only mirror into the Aspire parent console. The AppHost child
                                    // session keeps its own DAP event because it owns a separate
                                    // debug console: the extension starts it without
                                    // `DebugConsoleMode.MergeWithParent`, and VS Code maps an
                                    // unset console mode to a separate REPL.
                                    // https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/common/extHostDebugService.ts
                                    appHostTracker.onOutput?.(output, category);
                                }
                                return;
                            }

                            const notification: ServiceLogsNotification = {
                                notification_type: 'serviceLogs',
                                session_id: configuration.runId,
                                dcp_id: debugSessionId,
                                is_std_err: category === 'stderr',
                                log_message: removeTrailingNewline(output)
                            };

                            dcpServer.sendNotification(notification);
                        }
                    }

                    if (!hasDcpRunSession) {
                        return;
                    }

                    // Listen for process event with isRestart (if supported by adapter)
                    if (message.type === 'event' && message.event === 'process') {
                        // A new debuggee process invalidates any exit code captured from a prior run.
                        // Reset before the PID guard: `systemProcessId` is optional in DAP, so a
                        // restart reported without it must still clear the stale exit code.
                        debuggeeExitCode = undefined;
                        if (!configuration.isApphost) {
                            onDebuggeeProcess?.(
                                session,
                                typeof message.body?.systemProcessId === 'number'
                                    ? message.body.systemProcessId
                                    : undefined);
                        }

                        if (typeof message.body?.systemProcessId !== 'number') {
                            extensionLogOutputChannel.warn(`Debug session ${session.id} does not have a valid system process ID.`);
                            return;
                        }

                        if (!dcpServer) {
                            extensionLogOutputChannel.warn(dcpServerNotInitialized);
                            return;
                        }
                        const processNotification: ProcessRestartedNotification = {
                            notification_type: 'processRestarted',
                            session_id: configuration.runId,
                            dcp_id: debugSessionId,
                            pid: message.body.systemProcessId
                        };

                        dcpServer.sendNotification(processNotification);
                    }

                    if (message.type === 'event' && message.event === 'exited' && typeof message.body?.exitCode === 'number') {
                        if (!configuration.isApphost) {
                            onDebuggeeProcess?.(session, undefined);
                        }
                        debuggeeExitCode = message.body.exitCode;
                    }
                },
                onExit(code: number | undefined) {
                    if (!hasDcpRunSession) {
                        return;
                    }

                    // Browser adapters are server-hosted or can disconnect without exiting the
                    // launched browser. The root VS Code debug session owns their terminal
                    // notification, so an adapter exit must not report the run a second time.
                    if (configuration.resourceType === 'browser') {
                        return;
                    }

                    let exitCode = debuggeeExitCode ?? code;

                    // Exit code 143 should be treated as normal exit (SIGTERM) on macOS and Linux
                    if ((process.platform === 'darwin' || process.platform === 'linux') && exitCode === 143) {
                        exitCode = 0;
                    }

                    const notification: SessionTerminatedNotification = {
                        notification_type: 'sessionTerminated',
                        session_id: configuration.runId,
                        dcp_id: debugSessionId,
                        exit_code: exitCode ?? 0
                    };

                    dcpServer.sendNotification(notification);
                }
            };
        }
    });
}

function isDebugConfigurationWithId(session: vscode.DebugConfiguration): session is AspireResourceExtendedDebugConfiguration {
    return (session as AspireResourceExtendedDebugConfiguration).runId !== undefined;
}
