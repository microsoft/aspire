import express, { Request, Response, NextFunction } from 'express';
import https from 'https';
import WebSocket, { WebSocketServer } from 'ws';
import * as vscode from 'vscode';
import { createSelfSignedCertAsync, generateToken } from '../utils/security';
import { extensionLogOutputChannel } from '../utils/logging';
import { AspireResourceDebugSession, DcpServerConnectionInfo, ErrorDetails, ErrorResponse, ProcessRestartedNotification, RunSessionNotification, RunSessionPayload, ServiceLogsNotification, SessionMessageNotification, SessionTerminatedNotification } from './types';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { createDebugSessionConfiguration, getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { timingSafeEqual } from 'crypto';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { authorizationAndDcpHeadersRequired, authorizationHeaderMustStartWithBearer, encounteredErrorStartingResource, invalidOrMissingToken, invalidTokenLength } from '../loc/strings';

export default class AspireDcpServer {
    private readonly app: express.Express;
    private server: https.Server;
    private wss: WebSocketServer;
    private wsBySession: Map<string, WebSocket> = new Map();
    private pendingNotificationQueueByDcpId: Map<string, RunSessionNotification[]> = new Map();

    public readonly connectionInfo: DcpServerConnectionInfo;

    private constructor(
        info: DcpServerConnectionInfo,
        app: express.Express,
        server: https.Server,
        wss: WebSocketServer,
        wsBySession: Map<string, WebSocket>,
        pendingNotificationQueueByDcpId: Map<string, RunSessionNotification[]>) {
        this.connectionInfo = info;
        this.app = app;
        this.server = server;
        this.wss = wss;
        this.wsBySession = wsBySession;
        this.pendingNotificationQueueByDcpId = pendingNotificationQueueByDcpId;
    }

    static async create(getDebugSession: (debugSessionId: string) => AspireDebugSession | null): Promise<AspireDcpServer> {
        const runsBySession = new Map<string, AspireResourceDebugSession[]>();
        const wsBySession = new Map<string, WebSocket>();
        const pendingNotificationQueueByDcpId = new Map<string, RunSessionNotification[]>();

        return new Promise(async (resolve, reject) => {
            const token = generateToken();

            const app = express();
            app.use(express.json());

            function requireHeaders(req: Request, res: Response, next: NextFunction): void {
                const auth = req.header('Authorization');
                const dcpId = req.header('microsoft-developer-dcp-instance-id');
                if (!auth || !dcpId) {
                    respondWithError(res, 401, { error: { code: 'MissingHeaders', message: authorizationAndDcpHeadersRequired, details: [] } });
                    return;
                }

                if (auth.split('Bearer ').length !== 2) {
                    respondWithError(res, 401, { error: { code: 'InvalidAuthHeader', message: authorizationHeaderMustStartWithBearer, details: [] } });
                    return;
                }

                const bearerTokenBuffer = Buffer.from(auth.split('Bearer ')[1]);
                const expectedTokenBuffer = Buffer.from(token);

                if (bearerTokenBuffer.length !== expectedTokenBuffer.length) {
                    respondWithError(res, 401, { error: { code: 'InvalidToken', message: invalidTokenLength, details: [] } });
                    return;
                }

                // timingSafeEqual is used to verify that the tokens are equivalent in a way that mitigates timing attacks
                if (timingSafeEqual(bearerTokenBuffer, expectedTokenBuffer) === false) {
                    respondWithError(res, 401, { error: { code: 'InvalidToken', message: invalidOrMissingToken, details: [] } });
                    return;
                }

                next();
            }

            app.get("/telemetry/enabled", (req: Request, res: Response) => {
                // TODO enable dashboard telemetry
                res.json({ is_enabled: false });
            });

            app.get('/info', (req: Request, res: Response) => {
                res.json(getRunSessionInfo());
            });

            app.put('/run_session', requireHeaders, async (req: Request, res: Response) => {
                const payload: RunSessionPayload = req.body;
                const runId = generateRunId();
                const dcpId = req.header('microsoft-developer-dcp-instance-id') as string;
                const debugSessionId = getDcpIdPrefix(dcpId);
                const processes: AspireResourceDebugSession[] = [];

                if (!debugSessionId) {
                    const error: ErrorDetails = {
                        code: 'MissingDebugSessionId',
                        message: 'Missing valid DCP prefix corresponding to an Aspire debug session.',
                        details: []
                    };

                    extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${error.message}`);
                    const response: ErrorResponse = { error };
                    respondWithError(res, 400, response);
                    return;
                }

                const launchConfig = payload.launch_configurations[0];
                const foundDebuggerExtension = getResourceDebuggerExtensions().find(ext => ext.resourceType === launchConfig.type) ?? null;

                if (!foundDebuggerExtension) {
                    const error: ErrorDetails = {
                        code: 'UnsupportedLaunchConfiguration',
                        message: `Unsupported launch configuration type: ${launchConfig.type}`,
                        details: []
                    };

                    extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${error.message}`);
                    const response: ErrorResponse = { error };
                    respondWithError(res, 400, response);
                    return;
                }

                const aspireDebugSession = getDebugSession(debugSessionId);
                if (!aspireDebugSession) {
                    const error: ErrorDetails = {
                        code: 'DebugSessionNotFound',
                        message: `No Aspire debug session found for Debug Session ID ${debugSessionId}`,
                        details: []
                    };

                    extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${error.message}`);
                    const response: ErrorResponse = { error };
                    respondWithError(res, 500, response);
                    return;
                }

                try {
                    const config = await createDebugSessionConfiguration(
                        aspireDebugSession.configuration,
                        launchConfig,
                        payload.args ?? [],
                        payload.env ?? [],
                        { debug: launchConfig.mode === "Debug", runId, debugSessionId: dcpId, isApphost: false, debugSession: aspireDebugSession },
                        foundDebuggerExtension
                    );

                    const resourceDebugSession = await aspireDebugSession.startAndGetDebugSession(config);

                    if (!resourceDebugSession) {
                        // Clean up any processes associated with this run (registered by resource-type extensions)
                        cleanupRun(runId);

                        const error: ErrorDetails = {
                            code: 'DebugSessionFailed',
                            message: `Failed to start debug session for run ID ${runId}`,
                            details: []
                        };

                        extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${error.message}`);
                        const response: ErrorResponse = { error };
                        respondWithError(res, 500, response);
                        return;
                    }

                    processes.push(resourceDebugSession);
                    extensionLogOutputChannel.info(`Debugging session created with ID: ${runId}`);

                    runsBySession.set(runId, processes);
                    res.status(201).set('Location', `https://${req.get('host')}/run_session/${runId}`).end();
                    extensionLogOutputChannel.info(`New run session created with ID: ${runId}`);
                } catch (err) {
                    extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${err}`);

                    // Clean up any processes associated with this run (registered by resource-type extensions)
                    cleanupRun(runId);

                    // Notify DCP via WebSocket that the session terminated so it can update
                    // resource state, AND respond with HTTP 500 so the original POST /run_session
                    // request gets a proper error. Both are needed: the 500 tells DCP the launch
                    // failed synchronously, while sessionTerminated handles async cleanup.
                    const notification: SessionTerminatedNotification = {
                        notification_type: 'sessionTerminated',
                        session_id: runId,
                        dcp_id: dcpId,
                        exit_code: -1
                    };

                    const ws = wsBySession.get(dcpId);
                    if (ws && ws.readyState === WebSocket.OPEN) {
                        AspireDcpServer.sendNotificationCore(notification, ws);
                    } else {
                        pendingNotificationQueueByDcpId.set(dcpId, [...(pendingNotificationQueueByDcpId.get(dcpId) || []), notification]);
                    }

                    const error: ErrorDetails = {
                        code: 'DebugSessionFailed',
                        message: `Failed to start debug session for run ID ${runId}: ${err instanceof Error ? err.message : String(err)}`,
                        details: []
                    };

                    const response: ErrorResponse = { error };
                    respondWithError(res, 500, response);
                }
            });

            app.delete('/run_session/:id', requireHeaders, async (req: Request, res: Response) => {
                const runId = req.params.id;
                if (runsBySession.has(runId)) {
                    const baseDebugSessions = runsBySession.get(runId);
                    for (const debugSession of baseDebugSessions || []) {
                        debugSession.stopSession();
                    }

                    runsBySession.delete(runId);
                    res.status(200).end();
                } else {
                    res.status(204).end();
                }
            });


            const { key, cert, certBase64 } = await createSelfSignedCertAsync();
            const server = https.createServer({ key, cert }, app);
            const wss = new WebSocketServer({ noServer: true });

            server.on('upgrade', (request, socket, head) => {
                if (request.url?.startsWith('/run_session/notify')) {
                    wss.handleUpgrade(request, socket, head, (ws) => {
                        const dcpId = request.headers['microsoft-developer-dcp-instance-id'] as string;
                        extensionLogOutputChannel.info(`WebSocket connection established for DCP ID: ${dcpId}`);
                        wsBySession.set(dcpId, ws);

                        const pendingNotifications = pendingNotificationQueueByDcpId.get(dcpId);
                        if (pendingNotifications) {
                            for (const notification of pendingNotifications) {
                                AspireDcpServer.sendNotificationCore(notification, ws);
                            }

                            pendingNotificationQueueByDcpId.delete(dcpId);
                        }

                        ws.onclose = () => {
                            extensionLogOutputChannel.info(`WebSocket connection closed for DCP ID: ${dcpId}`);
                            wsBySession.delete(dcpId);
                        };
                    });
                } else {
                    socket.destroy();
                }
            });

            wss.on('connection', (ws: WebSocket) => {
                ws.send(JSON.stringify({ notification_type: 'connected' }) + '\n');
            });

            wss.on('message', (data) => {
                extensionLogOutputChannel.info(`Received message from WebSocket client: ${data}`);
            });

            server.listen(0, () => {
                const addr = server.address();
                if (typeof addr === 'object' && addr) {
                    extensionLogOutputChannel.info(`DCP server listening on port ${addr.port} (HTTPS)`);
                    const info: DcpServerConnectionInfo = {
                        address: `localhost:${addr.port}`,
                        token: token,
                        certificate: certBase64
                    };
                    resolve(new AspireDcpServer(info, app, server, wss, wsBySession, pendingNotificationQueueByDcpId));
                } else {
                    reject(new Error('Failed to get server address'));
                }
            });

            server.on('error', reject);
        });
    }

    sendNotification(notification: RunSessionNotification) {
        // If no WebSocket is available for the session, log a warning
        const ws = this.wsBySession.get(notification.dcp_id);
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            extensionLogOutputChannel.trace(`No WebSocket found for DCP ID: ${notification.dcp_id} or WebSocket is not open (state: ${ws?.readyState})`);
            this.pendingNotificationQueueByDcpId.set(notification.dcp_id, [...(this.pendingNotificationQueueByDcpId.get(notification.dcp_id) || []), notification]);
            return;
        }

        AspireDcpServer.sendNotificationCore(notification, ws);
    }

    static sendNotificationCore(notification: RunSessionNotification, ws: WebSocket) {
        // Send the notification to the WebSocket
        if (notification.notification_type === 'processRestarted') {
            const processNotification = notification as ProcessRestartedNotification;
            const message = JSON.stringify({
                notification_type: 'processRestarted',
                session_id: notification.session_id,
                pid: processNotification.pid
            });

            ws.send(message + '\n');
        }
        else if (notification.notification_type === 'sessionTerminated') {
            const sessionTerminated = notification as SessionTerminatedNotification;
            const message = JSON.stringify({
                notification_type: 'sessionTerminated',
                session_id: notification.session_id,
                exit_code: sessionTerminated.exit_code
            });

            ws.send(message + '\n');
        }
        else if (notification.notification_type === 'serviceLogs') {
            const serviceLogs = notification as ServiceLogsNotification;
            const message = JSON.stringify({
                notification_type: 'serviceLogs',
                session_id: notification.session_id,
                is_std_err: serviceLogs.is_std_err,
                log_message: serviceLogs.log_message
            });

            ws.send(message + '\n');
        }
    }

    public dispose(): void {
        // Send WebSocket close message to all clients before shutting down
        if (this.wss) {
            this.wss.clients.forEach(client => {
                if (client.readyState === WebSocket.OPEN) {
                    client.close(1000, 'DCP server shutting down');
                }
            });
            this.wss.close();
        }

        if (this.server) {
            this.server.close();
        }
    }
}

export function generateRunId(): string {
    return `run-${Math.random().toString(36).substring(2, 15)}`;
}

export function generateDcpIdPrefix(): string {
    return `aspire-extension-run-${Math.random().toString(36).substring(2, 15)}`;
}

function getDcpIdPrefix(dcpId: string): string | null {
    const regex = /^(aspire-extension-run-[a-z0-9]+)-.+$/;
    if (regex.test(dcpId)) {
        return dcpId.match(regex)![1];
    }

    return null;
}

function respondWithError(res: Response, statusCode: number, message: ErrorResponse): void {
    res.status(statusCode).json(message).end();
    vscode.window.showErrorMessage(encounteredErrorStartingResource(message.error.message));
}
