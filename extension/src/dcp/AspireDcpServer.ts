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
import { timingSafeEqual, randomBytes } from 'crypto';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { authorizationAndDcpHeadersRequired, authorizationHeaderMustStartWithBearer, authorizationHeaderRequired, encounteredErrorStartingResource, invalidOrMissingToken, invalidTokenLength } from '../loc/strings';
import { DashboardTelemetryPassthrough } from './DashboardTelemetryPassthrough';
import { sendTelemetryErrorEvent, sendTelemetryEvent } from '../utils/telemetry';

/**
 * Callbacks the DCP server invokes for cross-cutting telemetry concerns.
 * Kept as an interface so the constructor stays narrow and so tests can
 * supply no-op implementations.
 */
export interface DcpTelemetryHooks {
    /**
     * Called whenever a `PUT /run_session` request is accepted, regardless of
     * whether the underlying debugger extension launch succeeds. Used by the
     * meaningful-engagement reporter to count any external debug activation
     * as engagement.
     */
    onRunSessionAccepted?: (info: { resourceType: string; mode: string }) => void;
}

type DebugSessionAggregateStats = {
    totalChildSessions: number;
    distinctResourceTypes: Set<string>;
    anyNonZeroExit: boolean;
};

export default class AspireDcpServer {
    private readonly app: express.Express;
    private server: https.Server;
    private wss: WebSocketServer;
    private wsBySession: Map<string, WebSocket> = new Map();
    private pendingNotificationQueueByDcpId: Map<string, RunSessionNotification[]> = new Map();
    private readonly _dashboardTelemetry: DashboardTelemetryPassthrough;
    // Per-runId metadata for telemetry correlation between PUT /run_session and
    // the subsequent sessionTerminated WebSocket notification. We need to look
    // up the original event timing/labels when the session terminates, since
    // the WebSocket notification arrives without that context.
    private readonly _runTelemetryById: Map<string, { startTimeMs: number; resourceType: string; mode: string; debugSessionId: string }>;
    // Per AppHost debug-session aggregate stats accumulated across the lifetime of the
    // session. Used to emit the `debug/appHost/end` summary when an AppHost debug session
    // terminates. Entries are added on first run_session for a debugSessionId and removed
    // (and returned) by takeDebugSessionAggregateStats().
    private readonly _debugSessionStats: Map<string, DebugSessionAggregateStats>;

    public readonly connectionInfo: DcpServerConnectionInfo;

    private constructor(
        info: DcpServerConnectionInfo,
        app: express.Express,
        server: https.Server,
        wss: WebSocketServer,
        wsBySession: Map<string, WebSocket>,
        pendingNotificationQueueByDcpId: Map<string, RunSessionNotification[]>,
        dashboardTelemetry: DashboardTelemetryPassthrough,
        runTelemetryById: Map<string, { startTimeMs: number; resourceType: string; mode: string; debugSessionId: string }>,
        debugSessionStats: Map<string, DebugSessionAggregateStats>) {
        this.connectionInfo = info;
        this.app = app;
        this.server = server;
        this.wss = wss;
        this.wsBySession = wsBySession;
        this.pendingNotificationQueueByDcpId = pendingNotificationQueueByDcpId;
        this._dashboardTelemetry = dashboardTelemetry;
        this._runTelemetryById = runTelemetryById;
        this._debugSessionStats = debugSessionStats;
    }

    /**
     * Returns and clears accumulated per-AppHost-debug-session telemetry stats for the
     * given debug session id. Called from AspireDebugSession.dispose() to emit the
     * `debug/appHost/end` summary event. Returns undefined if no run_session was ever
     * accepted for this debug session.
     */
    takeDebugSessionAggregateStats(debugSessionId: string): { totalChildSessions: number; distinctResourceTypes: string[]; anyNonZeroExit: boolean } | undefined {
        const stats = this._debugSessionStats.get(debugSessionId);
        if (!stats) {
            return undefined;
        }
        this._debugSessionStats.delete(debugSessionId);
        return {
            totalChildSessions: stats.totalChildSessions,
            distinctResourceTypes: Array.from(stats.distinctResourceTypes).sort(),
            anyNonZeroExit: stats.anyNonZeroExit,
        };
    }

    recordAppHostProcessExit(debugSessionId: string, exitCode: number | null): void {
        if (exitCode === 0 || exitCode === null) {
            return;
        }

        const stats = this._getOrCreateDebugSessionStats(debugSessionId);
        stats.anyNonZeroExit = true;
    }

    private _getOrCreateDebugSessionStats(debugSessionId: string): DebugSessionAggregateStats {
        let stats = this._debugSessionStats.get(debugSessionId);
        if (!stats) {
            stats = { totalChildSessions: 0, distinctResourceTypes: new Set<string>(), anyNonZeroExit: false };
            this._debugSessionStats.set(debugSessionId, stats);
        }

        return stats;
    }

    static async create(getDebugSession: (debugSessionId: string) => AspireDebugSession | null, hooks: DcpTelemetryHooks = {}): Promise<AspireDcpServer> {
        const runsBySession = new Map<string, AspireResourceDebugSession[]>();
        const runTelemetryById = new Map<string, { startTimeMs: number; resourceType: string; mode: string; debugSessionId: string }>();
        const debugSessionStats = new Map<string, DebugSessionAggregateStats>();
        const getOrCreateDebugSessionStats = (debugSessionId: string): DebugSessionAggregateStats => {
            let aggregate = debugSessionStats.get(debugSessionId);
            if (!aggregate) {
                aggregate = { totalChildSessions: 0, distinctResourceTypes: new Set<string>(), anyNonZeroExit: false };
                debugSessionStats.set(debugSessionId, aggregate);
            }

            return aggregate;
        };
        const wsBySession = new Map<string, WebSocket>();
        const pendingNotificationQueueByDcpId = new Map<string, RunSessionNotification[]>();
        const dashboardTelemetry = new DashboardTelemetryPassthrough();

        return new Promise(async (resolve, reject) => {
            const token = generateToken();

            const app = express();
            app.use(express.json());

            // Validates an HTTP Authorization header of the form
            //   Authorization: Bearer <token>
            // per RFC 6750 §2.1. Returns a discriminated result describing
            // which validation step failed. Factored out so the two middlewares
            // below share identical parsing semantics (the prior
            // `.split('Bearer ').length === 2` check accepted other schemes
            // that happened to contain `Bearer ` as a substring, e.g.
            // `X-Bearer <token>`).
            const BEARER_PREFIX = 'Bearer ';
            function validateBearerToken(auth: string | undefined):
                | { kind: 'ok' }
                | { kind: 'missing' }
                | { kind: 'invalid_scheme' }
                | { kind: 'invalid_length' }
                | { kind: 'invalid_token' } {
                if (!auth) {
                    return { kind: 'missing' };
                }
                if (!auth.startsWith(BEARER_PREFIX) || auth.length === BEARER_PREFIX.length) {
                    return { kind: 'invalid_scheme' };
                }
                const candidateToken = Buffer.from(auth.slice(BEARER_PREFIX.length));
                const expectedToken = Buffer.from(token);
                if (candidateToken.length !== expectedToken.length) {
                    return { kind: 'invalid_length' };
                }
                // timingSafeEqual is used to verify that the tokens are equivalent in a way that mitigates timing attacks
                if (timingSafeEqual(candidateToken, expectedToken) === false) {
                    return { kind: 'invalid_token' };
                }
                return { kind: 'ok' };
            }

            // `validateBearerToken` only returns 'missing' when the Authorization
            // header is absent; the requireHeaders path catches that case inline
            // (with the combined message) before calling validateBearerToken.
            // Keep this helper Authorization-only for DCP endpoints that already
            // performed their own DCP instance-id validation.
            function respondToBearerFailure(res: Response, kind: 'missing' | 'invalid_scheme' | 'invalid_length' | 'invalid_token'): void {
                switch (kind) {
                    case 'missing':
                        respondWithError(res, 401, { error: { code: 'MissingHeaders', message: authorizationHeaderRequired, details: [] } });
                        return;
                    case 'invalid_scheme':
                        respondWithError(res, 401, { error: { code: 'InvalidAuthHeader', message: authorizationHeaderMustStartWithBearer, details: [] } });
                        return;
                    case 'invalid_length':
                        respondWithError(res, 401, { error: { code: 'InvalidToken', message: invalidTokenLength, details: [] } });
                        return;
                    case 'invalid_token':
                        respondWithError(res, 401, { error: { code: 'InvalidToken', message: invalidOrMissingToken, details: [] } });
                        return;
                }
            }

            function requireHeaders(req: Request, res: Response, next: NextFunction): void {
                const auth = req.header('Authorization');
                const dcpId = req.header('microsoft-developer-dcp-instance-id');
                if (!auth || !dcpId) {
                    respondWithError(res, 401, { error: { code: 'MissingHeaders', message: authorizationAndDcpHeadersRequired, details: [] } });
                    return;
                }

                const result = validateBearerToken(auth);
                if (result.kind !== 'ok') {
                    respondToBearerFailure(res, result.kind);
                    return;
                }

                next();
            }

            function respondWithTelemetryAuthError(res: Response, statusCode: number, code: string, message: string): void {
                res.status(statusCode).json({ error: { code, message, details: [] } }).end();
            }

            function respondToTelemetryBearerFailure(res: Response, kind: 'missing' | 'invalid_scheme' | 'invalid_length' | 'invalid_token'): void {
                switch (kind) {
                    case 'missing':
                        respondWithTelemetryAuthError(res, 401, 'MissingHeaders', authorizationAndDcpHeadersRequired);
                        return;
                    case 'invalid_scheme':
                        respondWithTelemetryAuthError(res, 401, 'InvalidAuthHeader', authorizationHeaderMustStartWithBearer);
                        return;
                    case 'invalid_length':
                        respondWithTelemetryAuthError(res, 401, 'InvalidToken', invalidTokenLength);
                        return;
                    case 'invalid_token':
                        respondWithTelemetryAuthError(res, 401, 'InvalidToken', invalidOrMissingToken);
                        return;
                }
            }

            function requireTelemetryHeaders(req: Request, res: Response, next: NextFunction): void {
                const auth = req.header('Authorization');
                const dcpId = req.header('microsoft-developer-dcp-instance-id');
                if (!auth || !dcpId) {
                    respondWithTelemetryAuthError(res, 401, 'MissingHeaders', authorizationAndDcpHeadersRequired);
                    return;
                }

                const result = validateBearerToken(auth);
                if (result.kind !== 'ok') {
                    respondToTelemetryBearerFailure(res, result.kind);
                    return;
                }

                const debugSessionId = getDcpIdPrefix(dcpId);
                if (!debugSessionId || !getDebugSession(debugSessionId)) {
                    respondWithTelemetryAuthError(res, 401, 'InvalidDcpInstanceId', 'Missing valid DCP prefix corresponding to an Aspire debug session.');
                    return;
                }

                next();
            }

            // Dashboard telemetry passthrough — mounts /telemetry/* including
            // the /telemetry/enabled handshake. Replaces the old hardcoded
            // is_enabled:false response so the dashboard's telemetry pipeline
            // can finally talk to the extension's reporter.
            dashboardTelemetry.register(app, requireTelemetryHeaders);

            // Per the DCP IDE-execution spec, GET /info requires both the
            // bearer token and the DCP instance id. See
            // docs/specs/IDE-execution.md (#ide-endpoint-information-request).
            // Without auth, any local process could enumerate which VS Code
            // language extensions are installed on the user's machine.
            app.get('/info', requireHeaders, (req: Request, res: Response) => {
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
                // Telemetry: clamp `launchConfig.mode` to the known
                // LaunchConfigurationMode values. It originates from the
                // CLI-controlled request body and feeds the `mode` dimension on
                // multiple events; without clamping an arbitrary string would
                // leak verbatim, mirroring the `supportedResourceType` clamp
                // below. `== null` catches both `undefined` and a malformed
                // JSON `null` (preserving the prior `?? 'Unknown'` behavior) and
                // keeps the 'Unknown' bucket; any other unexpected value
                // collapses to 'other'.
                const rawMode = launchConfig.mode;
                const mode = rawMode == null
                    ? 'Unknown'
                    : (rawMode === 'Debug' || rawMode === 'NoDebug' ? rawMode : 'other');
                // Telemetry: clamp `launchConfig.type` to the set of resource types we
                // actually understand. Unsupported types come from
                // `payload.launch_configurations[0].type` which is a CLI-controlled
                // string and could otherwise leak arbitrary content (custom resource
                // type names, typos) into telemetry. The supported set is the
                // discriminator we care about — "did the user run something we know
                // how to debug?" — and one bucket for everything else is enough.
                const supportedResourceType = foundDebuggerExtension ? launchConfig.type : 'unsupported';
                // Emit early — even unsupported resource types count as engagement
                // because the user did try to run something through us.
                hooks.onRunSessionAccepted?.({ resourceType: launchConfig.type, mode });
                const runSessionStartTimeMs = Date.now();
                sendTelemetryEvent('debug/runSession/start', {
                    resource_type: supportedResourceType,
                    debugger_extension_matched: foundDebuggerExtension ? 'true' : 'false',
                    mode,
                });

                // Emits a `debug/runSession/end` event paired with the start above and
                // updates the parent AppHost aggregate so failures captured on early-
                // return paths still surface in the `debug/appHost/end` summary. All
                // post-start failure paths in this handler must route through here so
                // we never leave an orphaned start event in the telemetry pipeline.
                const emitRunSessionFailureEnd = (endReason: string, errorKind?: string): void => {
                    const aggregate = getOrCreateDebugSessionStats(debugSessionId);
                    aggregate.totalChildSessions += 1;
                    aggregate.distinctResourceTypes.add(supportedResourceType);
                    aggregate.anyNonZeroExit = true;

                    sendTelemetryErrorEvent('debug/runSession/end', {
                        resource_type: supportedResourceType,
                        mode,
                        exit_code_bucket: 'nonzero',
                        end_reason: endReason,
                        ...(errorKind ? { error_kind: errorKind } : {}),
                    }, {
                        duration_ms: Date.now() - runSessionStartTimeMs,
                    });
                };

                if (!foundDebuggerExtension) {
                    emitRunSessionFailureEnd('unsupported_launch_config');
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
                    emitRunSessionFailureEnd('debug_session_not_found');
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
                        payload.args,
                        payload.env ?? [],
                        { debug: launchConfig.mode === "Debug", runId, debugSessionId: dcpId, isApphost: false, debugSession: aspireDebugSession },
                        foundDebuggerExtension
                    );

                    const resourceDebugSession = await aspireDebugSession.startAndGetDebugSession(config);

                    if (!resourceDebugSession) {
                        emitRunSessionFailureEnd('debugger_did_not_start');

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
                    runTelemetryById.set(runId, { startTimeMs: runSessionStartTimeMs, resourceType: supportedResourceType, mode, debugSessionId });

                    // Track aggregate stats for the parent AppHost debug session so we can
                    // emit a single `debug/appHost/end` summary when the AppHost terminates.
                    const aggregate = getOrCreateDebugSessionStats(debugSessionId);
                    aggregate.totalChildSessions += 1;
                    aggregate.distinctResourceTypes.add(supportedResourceType);

                    res.status(201).set('Location', `https://${req.get('host')}/run_session/${runId}`).end();
                    extensionLogOutputChannel.info(`New run session created with ID: ${runId}`);
                } catch (err) {
                    extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${err}`);

                    // Synchronous launch failure — emit the matching end event and update
                    // aggregate stats via the shared helper before responding so the eventual
                    // `debug/appHost/end` summary reflects the failure.
                    emitRunSessionFailureEnd('launch_failed', err instanceof Error ? err.name || 'Error' : typeof err);

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
                const runId = req.params.id as string;
                if (runsBySession.has(runId)) {
                    const baseDebugSessions = runsBySession.get(runId);
                    for (const debugSession of baseDebugSessions || []) {
                        debugSession.stopSession();
                    }

                    runsBySession.delete(runId);
                    // Map cleanup happens when the corresponding sessionTerminated
                    // notification is sent; don't pre-delete here or we'd miss the
                    // end event.
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
                    // Per the DCP IDE-execution spec, /run_session/notify
                    // upgrade requires both the bearer token and the DCP
                    // instance id headers. See
                    // docs/specs/IDE-execution.md (#subscribe-to-session-change-notifications-request).
                    //
                    // Without this check, any local actor able to reach our
                    // localhost port could:
                    //   - Subscribe to the notification stream and receive
                    //     `serviceLogs` (stdout/stderr of debugged user
                    //     processes) and `sessionTerminated` notifications
                    //     by guessing or predicting a `dcpId`.
                    //   - Hijack notification delivery for an active debug
                    //     session — `wsBySession.set(dcpId, ws)` below
                    //     replaces any existing entry, so a second connection
                    //     for the same `dcpId` silently steals all future
                    //     notifications from the legitimate DCP client.
                    const authHeader = request.headers['authorization'] as string | undefined;
                    const dcpId = request.headers['microsoft-developer-dcp-instance-id'] as string | undefined;
                    if (!dcpId) {
                        socket.write('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
                        socket.destroy();
                        return;
                    }
                    const authResult = validateBearerToken(authHeader);
                    if (authResult.kind !== 'ok') {
                        socket.write('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
                        socket.destroy();
                        return;
                    }
                    wss.handleUpgrade(request, socket, head, (ws) => {
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

            server.listen(0, 'localhost', () => {
                const addr = server.address();
                if (typeof addr === 'object' && addr) {
                    extensionLogOutputChannel.info(`DCP server listening on port ${addr.port} (HTTPS)`);
                    const info: DcpServerConnectionInfo = {
                        address: `localhost:${addr.port}`,
                        token: token,
                        certificate: certBase64
                    };
                    resolve(new AspireDcpServer(info, app, server, wss, wsBySession, pendingNotificationQueueByDcpId, dashboardTelemetry, runTelemetryById, debugSessionStats));
                } else {
                    reject(new Error('Failed to get server address'));
                }
            });

            server.on('error', reject);
        });
    }

    sendNotification(notification: RunSessionNotification) {
        // Emit a telemetry end event for session termination, regardless of
        // whether the WebSocket is currently connected. We do this here (and
        // not at the WebSocket-send call site) because every termination path
        // goes through sendNotification — the synchronous launch-failure path
        // in PUT /run_session goes through sendNotificationCore directly, and
        // already emits its own end event from the catch block.
        if (notification.notification_type === 'sessionTerminated') {
            const sessionTerminated = notification as SessionTerminatedNotification;
            const entry = this._runTelemetryById.get(notification.session_id);
            if (entry) {
                this._runTelemetryById.delete(notification.session_id);
                const durationMs = Date.now() - entry.startTimeMs;
                const exitCode = sessionTerminated.exit_code;
                const exitBucket = exitCode === 0 ? 'success' : exitCode === -1 ? 'canceled' : 'nonzero';
                // Route non-zero exits through the error-event channel so they get the
                // reporter's stricter scrubbing pass and are surfaced as errors in the
                // telemetry pipeline (consistent with the synchronous launch-failure path
                // above and the dashboard fault path in DashboardTelemetryPassthrough).
                const emitEnd = exitBucket === 'nonzero' ? sendTelemetryErrorEvent : sendTelemetryEvent;
                emitEnd('debug/runSession/end', {
                    resource_type: entry.resourceType,
                    mode: entry.mode,
                    exit_code_bucket: exitBucket,
                }, {
                    duration_ms: durationMs,
                    exit_code: exitCode,
                });

                // Surface a non-zero exit on the parent AppHost debug-session aggregate so
                // the eventual `debug/appHost/end` summary reflects whether any child
                // resource session ended unsuccessfully.
                if (exitBucket === 'nonzero') {
                    this.recordAppHostProcessExit(entry.debugSessionId, exitCode);
                }
            }
        }

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

        this._dashboardTelemetry.dispose();
    }
}

// Cryptographically-secure identifier generators. The DCP instance id is
// used as the keying material for routing notifications back to a specific
// debug session (`wsBySession.set(dcpId, ws)`) — a predictable id combined
// with the WebSocket upgrade endpoint would let a colocated process hijack
// the notification stream. `Math.random()` is NOT cryptographically secure
// (V8's xorshift128+ is predictable from a small number of outputs), so use
// `randomBytes` instead. 16 hex chars = 64 bits of true entropy.
//
// Returns only `[0-9a-f]` so the `getDcpIdPrefix` regex below
// (`aspire-extension-run-[a-z0-9]+`) keeps matching without changes.
export function generateRunId(): string {
    return `run-${randomBytes(8).toString('hex')}`;
}

export function generateDcpIdPrefix(): string {
    return `aspire-extension-run-${randomBytes(8).toString('hex')}`;
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
