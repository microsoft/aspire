import * as vscode from 'vscode';
import { ServiceLogsNotification, ProcessRestartedNotification, SessionTerminatedNotification, AspireResourceExtendedDebugConfiguration } from "../dcp/types";
import { extensionLogOutputChannel } from "../utils/logging";
import AspireDcpServer from '../dcp/AspireDcpServer';
import { removeTrailingNewline } from '../utils/strings';
import { dcpServerNotInitialized } from '../loc/strings';

/**
 * Callback invoked when a restart is requested on an app host debug session.
 * Return `true` to suppress VS Code's automatic child session restart.
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

export interface AppHostTrackerOptions {
    // VS Code invokes every factory registered for an adapter type for every matching
    // debug session. Scope AppHost output to the synthetic Aspire session that
    // registered this tracker so concurrent AppHosts cannot consume each other's logs.
    debugSessionId: string;
    onRestartRequested?: AppHostRestartHandler;
    onOutput?: AppHostOutputHandler;
    onTerminationRequested?: AppHostTerminationRequestHandler;
}

export function createDebugAdapterTracker(dcpServer: AspireDcpServer, debugAdapter: string, appHostTracker?: AppHostTrackerOptions): vscode.Disposable {
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
                        debuggeeExitCode = message.body.exitCode;
                    }
                },
                onExit(code: number | undefined) {
                    if (!hasDcpRunSession) {
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
