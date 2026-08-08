import express, { Request, Response, NextFunction } from 'express';
import https from 'https';
import WebSocket, { WebSocketServer } from 'ws';
import * as vscode from 'vscode';
import { createSelfSignedCertAsync, generateToken } from '../utils/security';
import { extensionLogOutputChannel } from '../utils/logging';
import { AspireResourceDebugSession, DcpServerConnectionInfo, ErrorDetails, ErrorResponse, ProcessRestartedNotification, RunSessionNotification, RunSessionPayload, ServiceLogsNotification, SessionMessageNotification, SessionTerminatedNotification } from './types';
import { RunSessionRecord, RunSessionRegistry, TerminationTrigger } from './RunSessionRegistry';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getResourceDebuggerExtensions, prepareDebugSession } from '../debugger/debuggerExtensions';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { timingSafeEqual, randomBytes } from 'crypto';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { authorizationAndDcpHeadersRequired, authorizationHeaderMustStartWithBearer, authorizationHeaderRequired, encounteredErrorStartingResource, invalidOrMissingToken, invalidTokenLength } from '../loc/strings';
import { DashboardTelemetryPassthrough } from './DashboardTelemetryPassthrough';
import { classifyError, sendTelemetryErrorEvent, sendTelemetryEvent } from '../utils/telemetry';

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

export interface DcpServerOptions {
    /**
     * How long a terminated run stays registered so a late debug-adapter exit can still
     * supply the true exit code for telemetry. See {@link RunSessionRegistry}.
     */
    runRetentionMs?: number;
}

type DebugSessionAggregateStats = {
    totalChildSessions: number;
    distinctResourceTypes: Set<string>;
    anyNonZeroExit: boolean;
};

/**
 * Close code sent to a `/run_session/notify` socket that a newer connection for the same
 * debug session has replaced. 4000-4999 is the range RFC 6455 reserves for private
 * application use, so it cannot collide with a protocol-defined code.
 * See https://datatracker.ietf.org/doc/html/rfc6455#section-7.4.2.
 */
const supersededNotificationSocketCloseCode = 4001;

interface AspireDcpServerParts {
    app: express.Express;
    dashboardTelemetry: DashboardTelemetryPassthrough;
    debugSessionStats: Map<string, DebugSessionAggregateStats>;
    info: DcpServerConnectionInfo;
    pendingNotificationsByRoutingKey: Map<string, RunSessionNotification[]>;
    runSessions: RunSessionRegistry;
    runTelemetryById: Map<string, RunTelemetryEntry>;
    server: https.Server;
    wsByRoutingKey: Map<string, WebSocket>;
    wss: WebSocketServer;
}

type RunTelemetryEntry = { startTimeMs: number; resourceType: string; mode: string; debugSessionId: string };

export default class AspireDcpServer {
    private readonly app: express.Express;
    private server: https.Server;
    private wss: WebSocketServer;
    // Notification transport is keyed by the stable debug-session prefix, never by the full
    // DCP instance ID. A DCP instance that reconnects (or is replaced within the same debug
    // session) lands in the same bucket, so queued notifications are never stranded under an
    // identity that will not come back. See getSessionRoutingKey.
    private wsByRoutingKey: Map<string, WebSocket> = new Map();
    private pendingNotificationsByRoutingKey: Map<string, RunSessionNotification[]> = new Map();
    private readonly _dashboardTelemetry: DashboardTelemetryPassthrough;
    private readonly _runSessions: RunSessionRegistry;
    private _disposed = false;
    // Per-runId metadata for telemetry correlation between PUT /run_session and
    // the subsequent sessionTerminated WebSocket notification. We need to look
    // up the original event timing/labels when the session terminates, since
    // the WebSocket notification arrives without that context.
    private readonly _runTelemetryById: Map<string, RunTelemetryEntry>;
    // Per AppHost debug-session aggregate stats accumulated across the lifetime of the
    // session. Used to emit the `debug/apphost/end` summary when an AppHost debug session
    // terminates. Entries are added on first run_session for a debugSessionId and removed
    // (and returned) by takeDebugSessionAggregateStats().
    private readonly _debugSessionStats: Map<string, DebugSessionAggregateStats>;

    public readonly connectionInfo: DcpServerConnectionInfo;

    private constructor(parts: AspireDcpServerParts) {
        this.connectionInfo = parts.info;
        this.app = parts.app;
        this.server = parts.server;
        this.wss = parts.wss;
        this.wsByRoutingKey = parts.wsByRoutingKey;
        this.pendingNotificationsByRoutingKey = parts.pendingNotificationsByRoutingKey;
        this._dashboardTelemetry = parts.dashboardTelemetry;
        this._runSessions = parts.runSessions;
        this._runTelemetryById = parts.runTelemetryById;
        this._debugSessionStats = parts.debugSessionStats;
    }

    /**
     * Returns and clears accumulated per-AppHost-debug-session telemetry stats for the
     * given debug session id. Called from AspireDebugSession.dispose() to emit the
     * `debug/apphost/end` summary event. Returns undefined if no run_session was ever
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

    static async create(
        getDebugSession: (debugSessionId: string) => AspireDebugSession | null,
        hooks: DcpTelemetryHooks = {},
        options: DcpServerOptions = {}): Promise<AspireDcpServer> {
        const runRetentionMs = options.runRetentionMs ?? 5_000;
        const runTelemetryById = new Map<string, RunTelemetryEntry>();
        const debugSessionStats = new Map<string, DebugSessionAggregateStats>();
        const getOrCreateDebugSessionStats = (debugSessionId: string): DebugSessionAggregateStats => {
            let aggregate = debugSessionStats.get(debugSessionId);
            if (!aggregate) {
                aggregate = { totalChildSessions: 0, distinctResourceTypes: new Set<string>(), anyNonZeroExit: false };
                debugSessionStats.set(debugSessionId, aggregate);
            }

            return aggregate;
        };
        const wsByRoutingKey = new Map<string, WebSocket>();
        const pendingNotificationsByRoutingKey = new Map<string, RunSessionNotification[]>();
        const dashboardTelemetry = new DashboardTelemetryPassthrough();
        let dcpServer: AspireDcpServer;
        // `dcpServer` is assigned once the HTTP server is listening, which always precedes any
        // request or adapter callback that can reach the registry.
        const runSessions = new RunSessionRegistry({
            recordCompletion: (runId, exitCode) => dcpServer._recordRunSessionCompletion(runId, exitCode),
            retentionMs: runRetentionMs,
            send: (sessionPrefix, notification) => dcpServer._deliver(sessionPrefix, notification),
        });

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
                sendTelemetryEvent('aspire/vscode/debug/runsession/start', {
                    resource_type: supportedResourceType,
                    debugger_extension_matched: foundDebuggerExtension ? 'true' : 'false',
                    mode,
                });

                // Emits a `debug/runsession/end` event paired with the start above and
                // updates the parent AppHost aggregate so failures captured on early-
                // return paths still surface in the `debug/apphost/end` summary. All
                // post-start failure paths in this handler must route through here so
                // we never leave an orphaned start event in the telemetry pipeline.
                const emitRunSessionFailureEnd = (endReason: string, errorKind?: string): void => {
                    runTelemetryById.delete(runId);
                    const aggregate = getOrCreateDebugSessionStats(debugSessionId);
                    aggregate.totalChildSessions += 1;
                    aggregate.distinctResourceTypes.add(supportedResourceType);
                    aggregate.anyNonZeroExit = true;

                    sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
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

                // Reserve the run before starting VS Code's debug session. The debug adapter
                // tracker is created during startup and resolves this run by `runId`, so the
                // registration must exist before startup can produce callbacks.
                //
                // `PUT /run_session` resources always launch through a debug adapter, so the
                // adapter's exit is the authoritative exit code for this run.
                const run = runSessions.register({
                    debugSessions: processes,
                    runId,
                    sessionPrefix: debugSessionId,
                    terminationTrigger: { kind: 'adapterExit' } satisfies TerminationTrigger,
                });
                runTelemetryById.set(runId, { startTimeMs: runSessionStartTimeMs, resourceType: supportedResourceType, mode, debugSessionId });

                try {
                    const preparedSession = await prepareDebugSession(
                        aspireDebugSession.configuration,
                        launchConfig,
                        payload.args,
                        payload.env ?? [],
                        { debug: launchConfig.mode === "Debug", runId, debugSessionId: dcpId, isApphost: false, debugSession: aspireDebugSession },
                        foundDebuggerExtension
                    );

                    if (run.lifecycle !== 'starting') {
                        cleanupRun(runId);
                        const error: ErrorDetails = {
                            code: 'RunSessionTerminated',
                            message: `Run session ${runId} was terminated while its debug session was starting.`,
                            details: []
                        };
                        res.status(409).json({ error }).end();
                        return;
                    }

                    const resourceDebugSession = preparedSession.alreadyStartedSession
                        ? aspireDebugSession.trackAlreadyStartedResourceSession(preparedSession.debugConfiguration, preparedSession.alreadyStartedSession)
                        : await aspireDebugSession.startAndGetDebugSession(preparedSession.debugConfiguration);

                    if (run.lifecycle !== 'starting') {
                        // DELETE can win while VS Code is still starting the adapter. A late
                        // success belongs to the already-terminated run, so stop it without
                        // publishing it as a live session.
                        if (resourceDebugSession) {
                            try {
                                void Promise.resolve(resourceDebugSession.stopSession()).catch(err => {
                                    extensionLogOutputChannel.warn(`Failed to stop late debug session for run ID ${runId}: ${err instanceof Error ? err.message : String(err)}`);
                                });
                            } catch (err) {
                                extensionLogOutputChannel.warn(`Failed to stop late debug session for run ID ${runId}: ${err instanceof Error ? err.message : String(err)}`);
                            }
                        }
                        cleanupRun(runId);

                        const error: ErrorDetails = {
                            code: 'RunSessionTerminated',
                            message: `Run session ${runId} was terminated while its debug session was starting.`,
                            details: []
                        };
                        res.status(409).json({ error }).end();
                        return;
                    }

                    if (!resourceDebugSession) {
                        runSessions.remove(runId);
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
                    run.lifecycle = 'running';
                    extensionLogOutputChannel.info(`Debugging session created with ID: ${runId}`);

                    // Track aggregate stats for the parent AppHost debug session so we can
                    // emit a single `debug/apphost/end` summary when the AppHost terminates.
                    const aggregate = getOrCreateDebugSessionStats(debugSessionId);
                    aggregate.totalChildSessions += 1;
                    aggregate.distinctResourceTypes.add(supportedResourceType);

                    res.status(201).set('Location', `https://${req.get('host')}/run_session/${runId}`).end();
                    extensionLogOutputChannel.info(`New run session created with ID: ${runId}`);
                } catch (err) {
                    if (run.lifecycle !== 'starting') {
                        cleanupRun(runId);
                        const error: ErrorDetails = {
                            code: 'RunSessionTerminated',
                            message: `Run session ${runId} was terminated while its debug session was starting.`,
                            details: []
                        };
                        res.status(409).json({ error }).end();
                        return;
                    }

                    extensionLogOutputChannel.error(`Error creating debug session ${runId}: ${err}`);

                    // Synchronous launch failure — emit the matching end event and update
                    // aggregate stats via the shared helper before responding so the eventual
                    // `debug/apphost/end` summary reflects the failure.
                    emitRunSessionFailureEnd('launch_failed', classifyError(err));

                    // Clean up any processes associated with this run (registered by resource-type extensions)
                    cleanupRun(runId);

                    // The HTTP failure and terminal notification are both required, but the
                    // adapter may still report its own exit after startup partially succeeded.
                    // Terminating through the registry makes that later callback a no-op.
                    runSessions.terminate(runId, undefined);

                    const error: ErrorDetails = {
                        code: 'DebugSessionFailed',
                        message: `Failed to start debug session for run ID ${runId}: ${err instanceof Error ? err.message : String(err)}`,
                        details: []
                    };

                    const response: ErrorResponse = { error };
                    respondWithError(res, 500, response);
                }
            });

            app.delete('/run_session/:id', requireHeaders, (req: Request, res: Response) => {
                const runId = req.params.id as string;
                const run = runSessions.get(runId);
                if (!run) {
                    // Per docs/specs/IDE-execution.md, an unknown session is 204 No Content, and
                    // DCP treats that as a successful stop. A terminated-and-evicted run is
                    // therefore indistinguishable from one that never existed, which is why no
                    // tombstone is needed to answer a retried DELETE correctly.
                    res.status(204).end();
                    return;
                }

                // Authorization is the debug-session prefix, matching the identity already used
                // by requireTelemetryHeaders and PUT /run_session. It deliberately does not
                // compare full DCP instance IDs: DCP may restart an instance within the same
                // debug session, and treating each instance as a distinct owner would require
                // handing run state between them.
                const dcpId = req.header('microsoft-developer-dcp-instance-id') as string;
                if (getSessionRoutingKey(dcpId) !== run.sessionPrefix) {
                    const error: ErrorDetails = {
                        code: 'RunSessionOwnerMismatch',
                        message: `Run session ${runId} is owned by a different Aspire debug session.`,
                        details: []
                    };
                    res.status(403).json({ error }).end();
                    return;
                }

                // DCP's DELETE contract is the protocol acknowledgement that the run has
                // terminated. Complete that contract before entering VS Code debugger
                // teardown, whose implementation may wait on another extension.
                runSessions.requestStop(runId);
                res.status(200).end();
                dcpServer._scheduleDebuggerTeardown(run);
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
                    //     session — the newest socket for a debug session owns
                    //     delivery, so a second connection takes over the stream
                    //     from the legitimate DCP client. The displaced socket is
                    //     closed rather than starved (see below), but the takeover
                    //     itself is only prevented by requiring these headers.
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
                        // Route on the debug-session prefix so a reconnecting or replaced DCP
                        // instance inherits the same queue instead of stranding it under an
                        // instance ID that never comes back.
                        const routingKey = getSessionRoutingKey(dcpId);
                        extensionLogOutputChannel.info(`WebSocket connection established for DCP ID: ${dcpId} (routing key: ${routingKey})`);

                        // A debug session has exactly one notification subscriber: the spec assigns
                        // /run_session/notify to DCP (docs/specs/IDE-execution.md
                        // #subscribe-to-session-change-notifications-request). The newest socket wins so
                        // a restarted DCP instance is never locked out by a half-open predecessor,
                        // but the displaced socket is closed here rather than left open. Leaving it
                        // open would silently redirect its log and terminal notification stream with
                        // no signal that it had stopped receiving anything; closing makes the loss of
                        // ownership positive and observable to whoever was displaced.
                        //
                        // Set before closing: the predecessor's onclose handler below only clears the
                        // map when it is still the registered socket, so the new owner survives.
                        const supersededWs = wsByRoutingKey.get(routingKey);
                        wsByRoutingKey.set(routingKey, ws);
                        if (supersededWs && supersededWs !== ws) {
                            extensionLogOutputChannel.warn(`DCP ID ${dcpId} superseded the notification socket for routing key ${routingKey}; closing the displaced connection.`);
                            supersededWs.close(supersededNotificationSocketCloseCode, 'Superseded by a newer DCP notification connection');
                        }

                        const pendingNotifications = pendingNotificationsByRoutingKey.get(routingKey);
                        if (pendingNotifications) {
                            for (const notification of pendingNotifications) {
                                AspireDcpServer.sendNotificationCore(notification, ws);
                            }

                            pendingNotificationsByRoutingKey.delete(routingKey);
                        }

                        ws.onclose = () => {
                            extensionLogOutputChannel.info(`WebSocket connection closed for DCP ID: ${dcpId}`);
                            if (wsByRoutingKey.get(routingKey) === ws) {
                                wsByRoutingKey.delete(routingKey);
                            }
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
                    dcpServer = new AspireDcpServer({
                        app,
                        dashboardTelemetry,
                        debugSessionStats,
                        info,
                        pendingNotificationsByRoutingKey,
                        runSessions,
                        runTelemetryById,
                        server,
                        wsByRoutingKey,
                        wss,
                    });
                    resolve(dcpServer);
                } else {
                    reject(new Error('Failed to get server address'));
                }
            });

            server.on('error', reject);
        });
    }

    public createRunSessionNotificationHandler(runId: string): (notification: RunSessionNotification) => void {
        // Resolve by runId on every callback rather than capturing the record. A tracker can
        // outlive its run, and an evicted run must drop its notifications instead of falling
        // through to generic AppHost delivery, which would bypass terminal deduplication.
        return notification => this._runSessions.notify(runId, notification);
    }

    public sendNotification(notification: RunSessionNotification): void {
        if (notification.session_id.length > 0) {
            this._runSessions.notify(notification.session_id, notification);
            return;
        }

        // AppHost-scoped notifications carry no run session (`session_id` is empty), so they
        // are delivered straight to the debug session's socket.
        this._deliver(getSessionRoutingKey(notification.dcp_id), notification);
    }

    /**
     * Terminates a run session that ends without a debug-adapter exit, such as a resource whose
     * lifetime is bound to a VS Code debug session. Safe to call for an unknown or already
     * terminated run.
     */
    public terminateRunSession(runId: string, exitCode?: number): void {
        this._runSessions.terminate(runId, exitCode);
    }

    private _scheduleDebuggerTeardown(run: RunSessionRecord): void {
        if (run.teardownStarted) {
            return;
        }
        run.teardownStarted = true;

        setImmediate(() => {
            if (this._disposed) {
                return;
            }

            for (const debugSession of run.debugSessions) {
                try {
                    void Promise.resolve(debugSession.stopSession()).catch(error => {
                        this._logDebuggerTeardownFailure(run.runId, error);
                    });
                } catch (error) {
                    this._logDebuggerTeardownFailure(run.runId, error);
                }
            }
        });
    }

    private _logDebuggerTeardownFailure(runId: string, error: unknown): void {
        extensionLogOutputChannel.warn(`Failed to stop debug session for run ID ${runId} after DELETE completed: ${error instanceof Error ? error.message : String(error)}`);
    }

    /**
     * Closes the telemetry pair for a run. Idempotent by design: a requested stop and a later
     * adapter exit both report completion, and only the first one to arrive is recorded.
     */
    private _recordRunSessionCompletion(runId: string, exitCode: number | undefined): void {
        const entry = this._runTelemetryById.get(runId);
        if (!entry) {
            return;
        }

        this._runTelemetryById.delete(runId);
        const durationMs = Date.now() - entry.startTimeMs;
        const exitBucket = exitCode === undefined
            ? 'unknown'
            : exitCode === 0
                ? 'success'
                : exitCode === -1
                    ? 'canceled'
                    : 'nonzero';
        // Route non-zero exits through the error-event channel so they are surfaced
        // as errors in the telemetry pipeline, consistent with the synchronous
        // launch-failure path above and the dashboard fault path.
        const emitEnd = exitBucket === 'nonzero' ? sendTelemetryErrorEvent : sendTelemetryEvent;
        emitEnd('aspire/vscode/debug/runsession/end', {
            resource_type: entry.resourceType,
            mode: entry.mode,
            exit_code_bucket: exitBucket,
        }, {
            duration_ms: durationMs,
            ...(exitCode === undefined ? {} : { exit_code: exitCode }),
        });

        // Surface a non-zero exit on the parent AppHost debug-session aggregate so
        // the eventual `debug/apphost/end` summary reflects whether any child
        // resource session ended unsuccessfully.
        if (exitBucket === 'nonzero' && exitCode !== undefined) {
            this.recordAppHostProcessExit(entry.debugSessionId, exitCode);
        }
    }

    /**
     * Delivers a notification to a debug session's current socket, queueing it under the
     * routing key when no socket is connected.
     *
     * Because the key is the stable debug-session prefix rather than a DCP instance ID, a
     * queue is always drained by the next connection from that debug session. Nothing can be
     * stranded under an identity that never returns.
     */
    private _deliver(routingKey: string, notification: RunSessionNotification): void {
        if (this._disposed) {
            return;
        }

        const ws = this.wsByRoutingKey.get(routingKey);
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            extensionLogOutputChannel.trace(`No open WebSocket for routing key ${routingKey} (state: ${ws?.readyState}); queueing notification.`);
            this.pendingNotificationsByRoutingKey.set(routingKey, [...(this.pendingNotificationsByRoutingKey.get(routingKey) || []), notification]);
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
                ...(sessionTerminated.exit_code === undefined ? {} : { exit_code: sessionTerminated.exit_code })
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
        if (this._disposed) {
            return;
        }

        // The registry emits a matching end event for every still-registered run, since
        // disposal can happen before either an adapter exit or the retention deadline.
        this._runSessions.dispose();

        this._disposed = true;

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

        this._runTelemetryById.clear();
        this.pendingNotificationsByRoutingKey.clear();
        this.wsByRoutingKey.clear();
        this._dashboardTelemetry.dispose();
    }
}

// Cryptographically-secure identifier generators. The debug-session prefix is
// the keying material for routing notifications back to a specific debug
// session (`wsByRoutingKey.set(getSessionRoutingKey(dcpId), ws)`) — a
// predictable id combined with the WebSocket upgrade endpoint would let a
// colocated process hijack the notification stream. `Math.random()` is NOT
// cryptographically secure (V8's xorshift128+ is predictable from a small
// number of outputs), so use `randomBytes` instead. 16 hex chars = 64 bits of
// true entropy.
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

/**
 * Reduces any DCP identity to the stable debug-session key used for notification routing
 * and DELETE authorization.
 *
 * DCP instance IDs are `<debug session prefix>-<instance suffix>`; the suffix changes when
 * DCP restarts an instance within the same debug session. Callers pass whichever form they
 * hold — the AppHost adapter carries the bare prefix, resource adapters and DCP requests
 * carry the full instance ID — and both resolve to the same key. A bare prefix has no
 * suffix to strip, so `getDcpIdPrefix` returns null and the value is already the key.
 */
export function getSessionRoutingKey(dcpId: string): string {
    return getDcpIdPrefix(dcpId) ?? dcpId;
}

function respondWithError(res: Response, statusCode: number, message: ErrorResponse): void {
    res.status(statusCode).json(message).end();
    vscode.window.showErrorMessage(encounteredErrorStartingResource(message.error.message));
}
