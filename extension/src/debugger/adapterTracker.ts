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

export function createDebugAdapterTracker(
    dcpServer: AspireDcpServer,
    debugAdapter: string,
    onAppHostRestartRequested?: AppHostRestartHandler,
    onAppHostOutput?: AppHostOutputHandler,
    onAppHostTerminationRequested?: AppHostTerminationRequestHandler): vscode.Disposable {
    return vscode.debug.registerDebugAdapterTrackerFactory(debugAdapter, {
        createDebugAdapterTracker(session: vscode.DebugSession) {
            const configuration = session.configuration;
            if (!isDebugConfigurationWithId(configuration) || configuration.debugSessionId === null) {
                return undefined;
            }
            const debugSessionId = configuration.debugSessionId;

            let debuggeeExitCode: number | undefined;
            let appHostExitObserved = false;

            return {
                onWillReceiveMessage: message => {
                    if (configuration.isApphost &&
                        (message.command === 'disconnect' || message.command === 'terminate') &&
                        !appHostExitObserved &&
                        debugSessionId) {
                        if (message.arguments?.restart) {
                            const shouldSuppress = onAppHostRestartRequested?.(debugSessionId) ?? false;
                            if (shouldSuppress) {
                                message.arguments.restart = false;
                            }
                        }
                        else if (message.command === 'terminate' ||
                            (message.command === 'disconnect' && message.arguments?.terminateDebuggee === true)) {
                            // VS Code can send disconnect({ terminateDebuggee: false }) only to
                            // clean up an adapter. Treat only explicit debuggee termination as user
                            // intent so a pre-start crash still records its launch failure.
                            onAppHostTerminationRequested?.(debugSessionId);
                        }
                    }
                },
                onDidSendMessage: message => {
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
                                onAppHostOutput?.(output, category);
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

                    // Listen for process event with isRestart (if supported by adapter)
                    if (message.type === 'event' && message.event === 'process') {
                        // A new debuggee process invalidates exit state captured from a prior run.
                        // Reset before the PID guard: `systemProcessId` is optional in DAP, so a
                        // restart reported without it must still clear the stale state.
                        debuggeeExitCode = undefined;
                        appHostExitObserved = false;

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
