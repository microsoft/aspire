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
export type AppHostOutputHandler = (output: string, category: string) => void;

export function createDebugAdapterTracker(dcpServer: AspireDcpServer, debugAdapter: string, onAppHostRestartRequested?: AppHostRestartHandler, onAppHostOutput?: AppHostOutputHandler): vscode.Disposable {
    return vscode.debug.registerDebugAdapterTrackerFactory(debugAdapter, {
        createDebugAdapterTracker(session: vscode.DebugSession) {
                return {
                    onWillReceiveMessage: message => {
                        if (!isDebugConfigurationWithId(session.configuration)) {
                            return;
                        }

                        // Detect restart requests on app host debug sessions.
                        // When the user clicks "restart" on the app host child session,
                        // suppress VS Code's automatic child restart so the Aspire debug
                        // session can restart entirely instead.
                        if (session.configuration.isApphost
                            && (message.command === 'disconnect' || message.command === 'terminate')
                            && message.arguments?.restart
                            && onAppHostRestartRequested
                            && session.configuration.debugSessionId) {
                            const shouldSuppress = onAppHostRestartRequested(session.configuration.debugSessionId);
                            if (shouldSuppress) {
                                message.arguments.restart = false;
                            }
                        }
                    },
                    onDidSendMessage: message => {
                        if (message.type === 'event' && message.event === 'output') {
                            if (!isDebugConfigurationWithId(session.configuration) || session.configuration.debugSessionId === null) {
                                extensionLogOutputChannel.warn(`Debug session ${session.id} does not have an attached run id.`);
                                return;
                            }

                            const { category, output } = message.body;
                            if (typeof output === 'string' && category !== 'telemetry') {
                                if (session.configuration.isApphost) {
                                    onAppHostOutput?.(output, category as string);
                                    return;
                                }

                                const notification: ServiceLogsNotification = {
                                    notification_type: 'serviceLogs',
                                    session_id: session.configuration.runId,
                                    dcp_id: session.configuration.debugSessionId,
                                    is_std_err: category === 'stderr',
                                    log_message: removeTrailingNewline(output)
                                };

                                dcpServer.sendNotification(notification);
                            }
                        }

                        // Listen for process event with isRestart (if supported by adapter)
                        if (message.type === 'event' && message.event === 'process') {
                            if (typeof message.body?.systemProcessId !== 'number') {
                                extensionLogOutputChannel.warn(`Debug session ${session.id} does not have a valid system process ID.`);
                                return;
                            }

                            if (!isDebugConfigurationWithId(session.configuration) || session.configuration.debugSessionId === null) {
                                extensionLogOutputChannel.warn(`Debug session ${session.id} does not have an attached run id.`);
                                return;
                            }

                            if (!dcpServer) {
                                extensionLogOutputChannel.warn(dcpServerNotInitialized);
                                return;
                            }
                            const processNotification: ProcessRestartedNotification = {
                                notification_type: 'processRestarted',
                                session_id: session.configuration.runId,
                                dcp_id: session.configuration.debugSessionId,
                                pid: message.body.systemProcessId
                            };

                            dcpServer.sendNotification(processNotification);
                        }
                    },
                    onExit(code: number | undefined) {
                        if (!isDebugConfigurationWithId(session.configuration) || session.configuration.debugSessionId === null) {
                            extensionLogOutputChannel.warn(`Debug session ${session.id} does not have an attached run id.`);
                        return;
                        }

                        // Exit code 143 should be treated as normal exit (SIGTERM) on macOS and Linux
                        if ((process.platform === 'darwin' || process.platform === 'linux') && code === 143) {
                            code = 0;
                        }

                        const notification: SessionTerminatedNotification = {
                            notification_type: 'sessionTerminated',
                            session_id: session.configuration.runId,
                            dcp_id: session.configuration.debugSessionId,
                            exit_code: code ?? 0
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
