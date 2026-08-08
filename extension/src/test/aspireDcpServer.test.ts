import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { once } from 'events';
import type { IncomingHttpHeaders } from 'http';
import * as https from 'https';
import * as sinon from 'sinon';
import type * as vscode from 'vscode';
import WebSocket from 'ws';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { nodeDebuggerExtension } from '../debugger/languages/node';
import { cleanupRun, registerRunCleanup } from '../debugger/runCleanupRegistry';
import AspireDcpServer, { DcpServerOptions, getSessionRoutingKey } from '../dcp/AspireDcpServer';
import type { AspireResourceDebugSession, AspireResourceExtendedDebugConfiguration, NodeLaunchConfiguration, ProcessRestartedNotification, RunSessionNotification, RunSessionPayload, ServiceLogsNotification, SessionMessageNotification, SessionTerminatedNotification } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';
import { __setReporterForTests } from '../utils/telemetry';

interface RecordedTelemetryEvent {
    isError?: boolean;
    measurements?: Record<string, number>;
    name: string;
    properties?: Record<string, string>;
}

class FakeTelemetryReporter {
    public readonly events: RecordedTelemetryEvent[] = [];
    public readonly telemetryLevel = 'all';

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true });
    }

    sendDangerousTelemetryException(error: Error, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name: error.name, properties, measurements, isError: true });
    }
}

interface DcpServerInternals {
    _runSessions: {
        _retentionTimers: Set<NodeJS.Timeout>;
        get(runId: string): { lifecycle: string; terminated: boolean } | undefined;
        size: number;
        values(): IterableIterator<{ runId: string }>;
    };
    _runTelemetryById: Map<string, unknown>;
    pendingNotificationsByRoutingKey: Map<string, RunSessionNotification[]>;
    server: https.Server;
    wsByRoutingKey: Map<string, WebSocket>;
}

interface Harness {
    dcpId: string;
    dcpSessionId: string;
    dcpServer: AspireDcpServer;
    disposed: boolean;
    queuedStopDebugging: sinon.SinonStub[];
    sockets: WebSocket[];
    startAndGetDebugSession: sinon.SinonStub;
    stopDebugging: sinon.SinonStub;
}

interface WireNotification {
    notification_type: string;
    session_id: string;
    [key: string]: unknown;
}

interface NotificationClient {
    notifications: WireNotification[];
    socket: WebSocket;
    waitForNotification(predicate?: (notification: WireNotification) => boolean): Promise<WireNotification>;
}

interface HttpResponse {
    body: string;
    statusCode: number | undefined;
    headers: IncomingHttpHeaders;
}

suite('Aspire DCP server', () => {
    let harness: Harness;
    let telemetryReporter: FakeTelemetryReporter;
    let restoreTelemetry: () => void;

    setup(async () => {
        telemetryReporter = new FakeTelemetryReporter();
        restoreTelemetry = __setReporterForTests(telemetryReporter as unknown as TelemetryReporter);
        harness = await startHarness();
    });

    teardown(async () => {
        try {
            await stopHarness(harness);
        } finally {
            sinon.restore();
            restoreTelemetry();
        }
    });

    test('reconnect drains queued notifications in order and excludes post-terminal events', async () => {
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        assert.ok(adapterNotificationHandler);
        await closeNotificationClient(harness, client);

        const restarted: ProcessRestartedNotification = {
            notification_type: 'processRestarted',
            session_id: runId,
            dcp_id: harness.dcpId,
            pid: 42,
        };
        const log: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: runId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'before termination',
        };
        const terminated: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 5,
        };
        const lateLog: ServiceLogsNotification = {
            ...log,
            log_message: 'after termination',
        };
        adapterNotificationHandler(restarted);
        adapterNotificationHandler(log);
        adapterNotificationHandler(terminated);
        adapterNotificationHandler(lateLog);

        // The queue is keyed on the debug-session prefix, and the registry stamps the
        // terminal notification it owns with that same prefix.
        assert.deepStrictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.get(harness.dcpSessionId), [
            restarted,
            log,
            { ...terminated, dcp_id: harness.dcpSessionId },
        ]);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'completed');

        const reconnectedClient = await openNotificationClient(harness);
        await drainNotifications(reconnectedClient);

        assert.deepStrictEqual(reconnectedClient.notifications, [
            {
                notification_type: 'processRestarted',
                session_id: runId,
                pid: 42,
            },
            {
                notification_type: 'serviceLogs',
                session_id: runId,
                is_std_err: false,
                log_message: 'before termination',
            },
            {
                notification_type: 'sessionTerminated',
                session_id: runId,
                exit_code: 5,
            },
        ]);
        assert.strictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.has(harness.dcpSessionId), false);
    });

    test('a replacement WebSocket for the same debug session supersedes the previous socket and closes it', async () => {
        const firstClient = await openNotificationClient(harness);
        const firstServerSocket = getInternals(harness.dcpServer).wsByRoutingKey.get(harness.dcpSessionId);
        assert.ok(firstServerSocket);
        assert.strictEqual(firstClient.socket.readyState, WebSocket.OPEN);

        // A second authenticated socket shares the debug-session prefix but reports a different
        // DCP instance ID, which is what a reconnecting or restarted DCP instance looks like. It
        // wins the delivery slot, and the displaced owner has to be told: leaving it open would
        // redirect every later log and terminal notification to the new socket while the old
        // client sat there believing it was still subscribed.
        const displacedClosed = once(firstClient.socket, 'close') as Promise<[number, Buffer]>;
        const replacementClient = await openNotificationClient(harness, `${harness.dcpSessionId}-replacement`);
        const replacementServerSocket = getInternals(harness.dcpServer).wsByRoutingKey.get(harness.dcpSessionId);
        assert.ok(replacementServerSocket);
        assert.notStrictEqual(replacementServerSocket, firstServerSocket);

        const [closeCode, closeReason] = await displacedClosed;
        assert.strictEqual(closeCode, 4001);
        assert.strictEqual(closeReason.toString(), 'Superseded by a newer DCP notification connection');
        assert.strictEqual(firstClient.socket.readyState, WebSocket.CLOSED);

        // Closing the predecessor must not clear the map entry the replacement just claimed.
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const notification = await replacementClient.waitForNotification();

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.deepStrictEqual(firstClient.notifications, []);
    });

    test('DELETE during startup prevents a late debug session from reviving the run', async () => {
        const startupCompleted = createDeferred<AspireResourceDebugSession | undefined>();
        const lateStopDebugging = sinon.stub().resolves();
        harness.startAndGetDebugSession.returns(startupCompleted.promise);
        const client = await openNotificationClient(harness);

        const createPromise = createRunSession(harness);
        await waitFor(() => getInternals(harness.dcpServer)._runSessions.size === 1);
        const [{ runId }] = getInternals(harness.dcpServer)._runSessions.values();

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const notification = await client.waitForNotification();

        startupCompleted.resolve({
            id: 'late-resource-debug-session',
            session: {} as AspireResourceDebugSession['session'],
            stopSession: createMemoizedStopSession(lateStopDebugging),
        });
        const createResponse = await createPromise;
        await waitFor(() => lateStopDebugging.called);
        await drainNotifications(client);

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(createResponse.statusCode, 409);
        assert.strictEqual(lateStopDebugging.calledOnce, true);
        assert.deepStrictEqual(client.notifications, [notification]);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
    });

    test('startup failure after DELETE does not send a second terminal notification', async () => {
        const startupCompleted = createDeferred<AspireResourceDebugSession | undefined>();
        harness.startAndGetDebugSession.returns(startupCompleted.promise);
        const client = await openNotificationClient(harness);

        const createPromise = createRunSession(harness);
        await waitFor(() => getInternals(harness.dcpServer)._runSessions.size === 1);
        const [{ runId }] = getInternals(harness.dcpServer)._runSessions.values();

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const notification = await client.waitForNotification();
        startupCompleted.reject(new Error('startup failed after stop'));
        const createResponse = await createPromise;
        await drainNotifications(client);

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(createResponse.statusCode, 409);
        assert.deepStrictEqual(client.notifications, [notification]);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
    });

    test('DELETE during debug configuration creation prevents debug session launch', async () => {
        const originalCreateDebugSessionConfigurationCallback = nodeDebuggerExtension.createDebugSessionConfigurationCallback;
        const configurationCreationStarted = createDeferred<void>();
        const configurationCreationCompleted = createDeferred<void>();
        nodeDebuggerExtension.createDebugSessionConfigurationCallback = async (...args) => {
            configurationCreationStarted.resolve();
            await configurationCreationCompleted.promise;
            return await originalCreateDebugSessionConfigurationCallback?.(...args);
        };

        try {
            const client = await openNotificationClient(harness);

            const createPromise = createRunSession(harness);
            await configurationCreationStarted.promise;
            const [{ runId }] = getInternals(harness.dcpServer)._runSessions.values();

            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            const notification = await client.waitForNotification();
            configurationCreationCompleted.resolve();
            const createResponse = await createPromise;
            await drainNotifications(client);

            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(createResponse.statusCode, 409);
            assert.strictEqual(harness.startAndGetDebugSession.notCalled, true);
            assert.deepStrictEqual(client.notifications, [notification]);
            assert.deepStrictEqual(notification, {
                notification_type: 'sessionTerminated',
                session_id: runId,
            });
            assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
        } finally {
            nodeDebuggerExtension.createDebugSessionConfigurationCallback = originalCreateDebugSessionConfigurationCallback;
        }
    });

    test('startup failure and a late adapter exit produce one terminal notification', async () => {
        let adapterNotificationHandler: ((notification: RunSessionNotification) => void) | undefined;
        harness.startAndGetDebugSession.callsFake((configuration: { runId: string }) => {
            adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(configuration.runId);
            return Promise.reject(new Error('debug adapter startup failed'));
        });
        const client = await openNotificationClient(harness);

        const createResponse = await createRunSession(harness);
        const notification = await client.waitForNotification();
        assert.ok(adapterNotificationHandler);
        const runId = notification.session_id;

        const lateAdapterNotification: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 9,
        };
        adapterNotificationHandler(lateAdapterNotification);
        adapterNotificationHandler(lateAdapterNotification);
        await drainNotifications(client);

        assert.strictEqual(createResponse.statusCode, 500);
        assert.deepStrictEqual(client.notifications, [notification]);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'completed');
        assert.strictEqual(telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length, 1);
    });

    test('DELETE notifies and responds before memoized debugger teardown completes', async () => {
        const stopStarted = createDeferred<void>();
        const stopCompleted = createDeferred<void>();
        const stopCompletedObserved = sinon.spy();
        harness.stopDebugging.callsFake(() => {
            stopStarted.resolve();
            return stopCompleted.promise.then(() => {
                stopCompletedObserved();
            });
        });
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const sendNotificationSpy = sinon.spy(AspireDcpServer, 'sendNotificationCore');

        const [deleteResponse, notification] = await Promise.all([
            request(harness, 'DELETE', `/run_session/${runId}`),
            client.waitForNotification(),
        ]);
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual(stopCompletedObserved.called, false);
        await stopStarted.promise;
        assert.strictEqual(harness.stopDebugging.calledOnce, true);
        assert.strictEqual(sendNotificationSpy.calledBefore(harness.stopDebugging), true);

        stopCompleted.resolve();
        await waitFor(() => stopCompletedObserved.called);
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [notification]);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
    });

    test('repeated DELETE does not retry production-memoized debugger teardown', async () => {
        const stopFailure = new Error('memoized stop failed');
        harness.stopDebugging.rejects(stopFailure);
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);

        const firstDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(firstDeleteResponse.statusCode, 200);
        const notification = await client.waitForNotification();
        const retryResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        await waitFor(() => warn.called);

        assert.strictEqual(retryResponse.statusCode, 200);
        assert.strictEqual(harness.stopDebugging.calledOnce, true);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [notification]);
    });

    test('requested stop is terminal on the notification stream and records later adapter completion once', async () => {
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const requestedStopNotification = await client.waitForNotification();
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(requestedStopNotification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        await closeNotificationClient(harness, client);

        const shutdownLog: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: runId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'shutdown output',
        };
        const restarted: ProcessRestartedNotification = {
            notification_type: 'processRestarted',
            session_id: runId,
            dcp_id: harness.dcpId,
            pid: 42,
        };
        const sessionMessage: SessionMessageNotification = {
            notification_type: 'sessionMessage',
            session_id: runId,
            dcp_id: harness.dcpId,
            level: 'info',
            message: 'shutdown message',
            details: [],
        };
        adapterNotificationHandler(shutdownLog);
        adapterNotificationHandler(restarted);
        adapterNotificationHandler(sessionMessage);

        const completed: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 0,
        };
        adapterNotificationHandler(completed);
        const duplicateCompletion: SessionTerminatedNotification = {
            ...completed,
            exit_code: 5,
        };
        adapterNotificationHandler(duplicateCompletion);

        assert.strictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.has(harness.dcpSessionId), false);
        const reconnectedClient = await openNotificationClient(harness);
        await drainNotifications(reconnectedClient);
        assert.deepStrictEqual(reconnectedClient.notifications, []);

        const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(runSessionEndEvents.length, 1);
        assert.deepStrictEqual(runSessionEndEvents[0].properties, {
            resource_type: 'node',
            mode: 'Debug',
            exit_code_bucket: 'success',
        });
        assert.strictEqual(runSessionEndEvents[0].measurements?.exit_code, 0);
    });

    test('DELETE preserves existing, completed, and unknown status semantics', async () => {
        const clock = sinon.useFakeTimers({
            shouldClearNativeTimers: true,
            toFake: ['setTimeout', 'clearTimeout'],
        });
        const client = await openNotificationClient(harness);
        const unknownResponse = await request(harness, 'DELETE', '/run_session/unknown');
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);

        const firstDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        await client.waitForNotification();
        const duplicateDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const completedNotification: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 0,
        };
        adapterNotificationHandler(completedNotification);
        const completedDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);

        await clock.tickAsync(5_000);
        const expiredDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);

        assert.deepStrictEqual({
            unknown: unknownResponse.statusCode,
            first: firstDeleteResponse.statusCode,
            duplicate: duplicateDeleteResponse.statusCode,
            completed: completedDeleteResponse.statusCode,
            expired: expiredDeleteResponse.statusCode,
        }, {
            unknown: 204,
            first: 200,
            duplicate: 200,
            completed: 200,
            expired: 204,
        });
    });

    test('DELETE contains a synchronous debugger teardown failure after responding', async () => {
        harness.stopDebugging.throws(new Error('stop threw synchronously'));
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(deleteResponse.statusCode, 200);
        const notification = await client.waitForNotification();
        await waitFor(() => warn.called);
        const retryResponse = await request(harness, 'DELETE', `/run_session/${runId}`);

        assert.strictEqual(retryResponse.statusCode, 200);
        assert.strictEqual(harness.stopDebugging.calledOnce, true);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.match(warn.firstCall.args[0], /stop threw synchronously/);
    });

    test('DELETE from a replacement DCP instance in the same debug session stops the run', async () => {
        const replacementDcpId = `${harness.dcpSessionId}-replacement`;
        const ownerClient = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        await closeNotificationClient(harness, ownerClient);
        const replacementClient = await openNotificationClient(harness, replacementDcpId);

        // DCP can restart an instance within the same debug session, which changes the
        // instance ID suffix. Authorization is the debug-session prefix, so the replacement
        // stops the run its predecessor started.
        const replacementResponse = await request(harness, 'DELETE', `/run_session/${runId}`, undefined, replacementDcpId);
        const notification = await replacementClient.waitForNotification();

        assert.strictEqual(replacementResponse.statusCode, 200);
        assert.strictEqual(harness.stopDebugging.calledOnce, true);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.deepStrictEqual(ownerClient.notifications, []);
    });

    test('DELETE without any connected WebSocket queues the terminal notification for the next instance', async () => {
        const replacementDcpId = `${harness.dcpSessionId}-replacement`;
        const ownerClient = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        await closeNotificationClient(harness, ownerClient);

        const log: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: runId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'before the instance went away',
        };
        adapterNotificationHandler(log);

        // The DELETE arrives while no socket is connected, so both notifications queue under
        // the debug-session prefix rather than under a DCP instance ID that may never return.
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.get(harness.dcpSessionId), [
            log,
            {
                notification_type: 'sessionTerminated',
                session_id: runId,
                dcp_id: harness.dcpSessionId,
            },
        ]);

        // A *different* DCP instance ID reconnects. The queue is keyed on the shared prefix,
        // so it drains here instead of being stranded under the previous instance ID.
        const replacementClient = await openNotificationClient(harness, replacementDcpId);
        await replacementClient.waitForNotification(notification => notification.notification_type === 'sessionTerminated');

        assert.deepStrictEqual(replacementClient.notifications, [
            {
                notification_type: 'serviceLogs',
                session_id: runId,
                is_std_err: false,
                log_message: 'before the instance went away',
            },
            {
                notification_type: 'sessionTerminated',
                session_id: runId,
            },
        ]);
        assert.strictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.has(harness.dcpSessionId), false);
    });

    test('a repeated DELETE from a replacement does not lose the queued terminal notification', async () => {
        const replacementDcpId = `${harness.dcpSessionId}-replacement`;
        const ownerClient = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        await closeNotificationClient(harness, ownerClient);

        // Sequence from https://github.com/microsoft/aspire/pull/19125#discussion_r3735627366:
        // the first instance stops the run while its socket is unavailable, then a replacement
        // instance retries the DELETE and is answered 200 without a second notification.
        const firstResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(firstResponse.statusCode, 200);

        const replacementClient = await openNotificationClient(harness, replacementDcpId);
        const replacementResponse = await request(harness, 'DELETE', `/run_session/${runId}`, undefined, replacementDcpId);
        await replacementClient.waitForNotification(notification => notification.notification_type === 'sessionTerminated');

        // The single notification still reaches DCP because it was queued under the debug
        // session, not under the instance that requested the stop.
        assert.strictEqual(replacementResponse.statusCode, 200);
        assert.strictEqual(harness.stopDebugging.calledOnce, true);
        assert.deepStrictEqual(replacementClient.notifications, [
            {
                notification_type: 'sessionTerminated',
                session_id: runId,
            },
        ]);
    });

    test('a queued notification for another run survives a DCP instance replacement', async () => {
        const replacementDcpId = `${harness.dcpSessionId}-replacement`;
        const ownerClient = await openNotificationClient(harness);
        const stoppedCreateResponse = await createRunSession(harness);
        const stoppedLocation = stoppedCreateResponse.headers.location;
        assert.ok(stoppedLocation);
        const stoppedRunId = stoppedLocation.substring(stoppedLocation.lastIndexOf('/') + 1);
        const retainedCreateResponse = await createRunSession(harness);
        const retainedLocation = retainedCreateResponse.headers.location;
        assert.ok(retainedLocation);
        const retainedRunId = retainedLocation.substring(retainedLocation.lastIndexOf('/') + 1);
        const retainedNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(retainedRunId);
        await closeNotificationClient(harness, ownerClient);

        const retainedLog: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: retainedRunId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'belongs to a run that is still going',
        };
        retainedNotificationHandler(retainedLog);
        const stopResponse = await request(harness, 'DELETE', `/run_session/${stoppedRunId}`);
        assert.strictEqual(stopResponse.statusCode, 200);

        const replacementClient = await openNotificationClient(harness, replacementDcpId);
        await replacementClient.waitForNotification(notification => notification.notification_type === 'sessionTerminated');

        // Every run in a debug session shares one queue, so stopping one run neither strands
        // nor drops the other run's traffic.
        assert.deepStrictEqual(replacementClient.notifications, [
            {
                notification_type: 'serviceLogs',
                session_id: retainedRunId,
                is_std_err: false,
                log_message: 'belongs to a run that is still going',
            },
            {
                notification_type: 'sessionTerminated',
                session_id: stoppedRunId,
            },
        ]);
    });

    test('DELETE succeeds while the only WebSocket for the debug session is closing', async () => {
        const ownerClient = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const ownerWebSocket = getInternals(harness.dcpServer).wsByRoutingKey.get(harness.dcpSessionId);
        assert.ok(ownerWebSocket);

        const ownerClosed = once(ownerClient.socket, 'close');
        ownerClient.socket.pause();
        ownerWebSocket.close();
        assert.strictEqual(ownerWebSocket.readyState, WebSocket.CLOSING);

        try {
            // A closing socket must not block the stop: the run terminates and the terminal
            // notification waits in the debug session's queue for the next connection.
            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(harness.stopDebugging.calledOnce, true);
            assert.deepStrictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.get(harness.dcpSessionId), [
                {
                    notification_type: 'sessionTerminated',
                    session_id: runId,
                    dcp_id: harness.dcpSessionId,
                },
            ]);
        } finally {
            ownerClient.socket.resume();
            await ownerClosed;
        }
    });

    test('DELETE rejects a DCP instance that does not own the run', async () => {
        const intruderDcpId = 'aspire-extension-run-foreign-intruder';
        const ownerClient = await openNotificationClient(harness);
        const intruderClient = await openNotificationClient(harness, intruderDcpId);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);

        const intruderResponse = await request(harness, 'DELETE', `/run_session/${runId}`, undefined, intruderDcpId);

        assert.strictEqual(intruderResponse.statusCode, 403);
        assert.deepStrictEqual(JSON.parse(intruderResponse.body), {
            error: {
                code: 'RunSessionOwnerMismatch',
                message: `Run session ${runId} is owned by a different Aspire debug session.`,
                details: [],
            },
        });
        assert.strictEqual(harness.stopDebugging.called, false);
        await Promise.all([drainNotifications(ownerClient), drainNotifications(intruderClient)]);
        assert.deepStrictEqual(ownerClient.notifications, []);
        assert.deepStrictEqual(intruderClient.notifications, []);

        const ownerResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const ownerNotification = await ownerClient.waitForNotification();

        assert.strictEqual(ownerResponse.statusCode, 200);
        assert.strictEqual(harness.stopDebugging.calledOnce, true);
        assert.deepStrictEqual(ownerNotification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        await drainNotifications(intruderClient);
        assert.deepStrictEqual(intruderClient.notifications, []);
    });

    test('requested stop records the adapter exit when it arrives during retention', async () => {
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        assert.ok(adapterNotificationHandler);

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const requestedStopNotification = await client.waitForNotification();

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(requestedStopNotification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual((getInternals(harness.dcpServer)._runSessions.get(runId) !== undefined), true);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.has(runId), true);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 1);
        assert.strictEqual(telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length, 0);

        const lateAdapterNotification: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 17,
        };
        adapterNotificationHandler(lateAdapterNotification);
        adapterNotificationHandler(lateAdapterNotification);
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [requestedStopNotification]);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.has(runId), false);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'completed');
        const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.deepStrictEqual(runSessionEndEvents, [{
            name: 'aspire/vscode/debug/runsession/end',
            properties: {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'nonzero',
            },
            measurements: {
                duration_ms: runSessionEndEvents[0]?.measurements?.duration_ms,
                exit_code: 17,
            },
            isError: true,
        }]);
        assert.strictEqual(harness.dcpServer.takeDebugSessionAggregateStats('aspire-extension-run-test')?.anyNonZeroExit, true);
    });

    test('a requested stop with no adapter exit records one canceled run session at the retention deadline', async () => {
        await stopHarness(harness);
        harness = await startHarness({ runRetentionMs: 1_000 });
        const clock = sinon.useFakeTimers({
            shouldClearNativeTimers: true,
            toFake: ['setTimeout', 'clearTimeout'],
        });
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const lateAdapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        assert.ok(lateAdapterNotificationHandler);

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const notification = await client.waitForNotification();

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        // The stop is already terminal on the notification stream; retention only keeps the
        // record addressable so a later adapter exit can still supply the real exit code.
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 1);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.has(runId), true);

        await clock.tickAsync(1_000);

        const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.deepStrictEqual(runSessionEndEvents, [{
            name: 'aspire/vscode/debug/runsession/end',
            properties: {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'canceled',
            },
            measurements: {
                duration_ms: runSessionEndEvents[0]?.measurements?.duration_ms,
                exit_code: -1,
            },
        }]);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.has(runId), false);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 0);

        const lateAdapterNotification: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 17,
        };
        lateAdapterNotificationHandler(lateAdapterNotification);
        lateAdapterNotificationHandler(lateAdapterNotification);
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [notification]);
        assert.strictEqual(telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length, 1);
    });

    test('late adapter tracker after retention cannot duplicate a requested stop', async () => {
        await stopHarness(harness);
        harness = await startHarness({ runRetentionMs: 5_000 });
        const clock = sinon.useFakeTimers({
            shouldClearNativeTimers: true,
            toFake: ['setTimeout', 'clearTimeout'],
        });
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const notification = await client.waitForNotification();
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 1);

        await clock.tickAsync(5_000);
        await waitFor(
            () => getInternals(harness.dcpServer)._runSessions.get(runId) === undefined,
            `run ${runId} eviction`);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 0);

        const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(runSessionEndEvents, [{
            name: 'aspire/vscode/debug/runsession/end',
            properties: {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'canceled',
            },
            measurements: {
                duration_ms: runSessionEndEvents[0]?.measurements?.duration_ms,
                exit_code: -1,
            },
        }]);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.has(runId), false);

        const lateAdapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        assert.ok(lateAdapterNotificationHandler);
        const lateAdapterNotification: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 17,
        };
        lateAdapterNotificationHandler(lateAdapterNotification);
        lateAdapterNotificationHandler(lateAdapterNotification);
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [notification]);
        assert.strictEqual(telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length, 1);
    });

    test('already-started resource termination after retention cannot duplicate a requested stop', async () => {
        await stopHarness(harness);
        harness = await startHarness({ runRetentionMs: 5_000 });
        const clock = sinon.useFakeTimers({
            shouldClearNativeTimers: true,
            toFake: ['setTimeout', 'clearTimeout'],
        });
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const resourceTermination = createDeferred<number>();
        const aspireDebugSession = createAspireDebugSessionForRunSessionTests(harness.dcpServer);
        const debugConfig = {
            runId,
            debugSessionId: harness.dcpId,
            type: 'coreclr',
            name: 'Already Started Resource',
            request: 'launch',
        } as AspireResourceExtendedDebugConfiguration;

        aspireDebugSession.trackAlreadyStartedResourceSession(debugConfig, {
            id: runId,
            processId: 4242,
            session: { id: 'already-started-resource' } as vscode.DebugSession,
            stopSession: sinon.stub().resolves(),
            termination: resourceTermination.promise,
        });
        const processNotification = await client.waitForNotification(notification => notification.notification_type === 'processRestarted');
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const requestedStopNotification = await client.waitForNotification(notification => notification.notification_type === 'sessionTerminated');
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 1);

        await clock.tickAsync(5_000);
        await waitFor(
            () => getInternals(harness.dcpServer)._runSessions.get(runId) === undefined,
            `run ${runId} eviction`);
        resourceTermination.resolve(17);
        await resourceTermination.promise;
        await drainNotifications(client);
        aspireDebugSession.dispose();

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(processNotification, {
            notification_type: 'processRestarted',
            session_id: runId,
            pid: 4242,
        });
        assert.deepStrictEqual(requestedStopNotification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.deepStrictEqual(client.notifications, [
            processNotification,
            requestedStopNotification,
        ]);
        assert.strictEqual(telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length, 1);
    });

    test('adapter exit before DELETE keeps its actual exit code and still tears down the debugger', async () => {
        const intruderClient = await openNotificationClient(harness, 'aspire-extension-run-foreign-intruder');
        const client = await openNotificationClient(harness);
        let runId = '';
        let cleanupCalled = false;
        const resourceTeardown = sinon.stub().callsFake(() => {
            cleanupRun(runId);
            return Promise.resolve();
        });
        const createResponse = await createRunSession(harness, resourceTeardown);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        registerRunCleanup(runId, () => {
            cleanupCalled = true;
        });
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        assert.ok(adapterNotificationHandler);

        try {
            const adapterNotification: SessionTerminatedNotification = {
                notification_type: 'sessionTerminated',
                session_id: runId,
                dcp_id: 'aspire-extension-run-foreign-intruder',
                exit_code: 23,
            };
            adapterNotificationHandler(adapterNotification);
            const notification = await client.waitForNotification();

            assert.deepStrictEqual(notification, {
                notification_type: 'sessionTerminated',
                session_id: runId,
                exit_code: 23,
            });

            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            const repeatedDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            adapterNotificationHandler(adapterNotification);
            await waitFor(() => resourceTeardown.called, `debugger teardown for completed run ${runId}`);
            await Promise.all([drainNotifications(client), drainNotifications(intruderClient)]);

            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(repeatedDeleteResponse.statusCode, 200);
            assert.strictEqual(resourceTeardown.calledOnce, true);
            assert.strictEqual(cleanupCalled, true);
            assert.deepStrictEqual(client.notifications, [notification]);
            assert.deepStrictEqual(intruderClient.notifications, []);
            assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'completed');
            const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
            assert.strictEqual(runSessionEndEvents.length, 1);
            assert.strictEqual(runSessionEndEvents[0].measurements?.exit_code, 23);
            assert.strictEqual(runSessionEndEvents[0].isError, true);
        } finally {
            cleanupRun(runId);
        }
    });

    test('terminateRunSession terminates a live run once and ignores unknown runs', async () => {
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);

        // The entry point for run sessions that end without a debug adapter exit. Termination
        // is single-shot inside the registry, so callers do not track what they have sent.
        harness.dcpServer.terminateRunSession(runId, 0);
        harness.dcpServer.terminateRunSession(runId, 0);
        harness.dcpServer.terminateRunSession('run-does-not-exist', 0);
        const notification = await client.waitForNotification();
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [notification]);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
            exit_code: 0,
        });
        const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(runSessionEndEvents.length, 1);
        assert.strictEqual(runSessionEndEvents[0].measurements?.exit_code, 0);

        // The run is still addressable during retention, so DCP's follow-up DELETE is
        // answered 200 rather than 204.
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(deleteResponse.statusCode, 200);
    });

    test('late debugger teardown rejection does not revive or duplicate a terminated run', async () => {
        const stopStarted = createDeferred<void>();
        const stopCompleted = createDeferred<void>();
        harness.stopDebugging.callsFake(() => {
            stopStarted.resolve();
            return stopCompleted.promise;
        });
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const deletePromise = request(harness, 'DELETE', `/run_session/${runId}`);
        const notification = await client.waitForNotification();
        const deleteResponse = await deletePromise;
        await stopStarted.promise;
        stopCompleted.reject(new Error('stop rejected after adapter exit'));
        await waitFor(() => warn.called);
        await drainNotifications(client);

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(client.notifications, [notification]);
        assert.deepStrictEqual(notification, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.get(runId)?.lifecycle, 'stopRequested');
    });

    test('terminal state and cleanup are isolated across concurrent runs', async () => {
        const firstStopSession = sinon.stub().resolves();
        const secondStopSession = sinon.stub().resolves();
        const client = await openNotificationClient(harness);
        const firstCreateResponse = await createRunSession(harness, firstStopSession);
        const secondCreateResponse = await createRunSession(harness, secondStopSession);
        const firstLocation = firstCreateResponse.headers.location;
        const secondLocation = secondCreateResponse.headers.location;
        assert.ok(firstLocation);
        assert.ok(secondLocation);
        const firstRunId = firstLocation.substring(firstLocation.lastIndexOf('/') + 1);
        const secondRunId = secondLocation.substring(secondLocation.lastIndexOf('/') + 1);
        const firstHandler = harness.dcpServer.createRunSessionNotificationHandler(firstRunId);
        const secondHandler = harness.dcpServer.createRunSessionNotificationHandler(secondRunId);
        assert.ok(firstHandler);
        assert.ok(secondHandler);

        const firstDeleteResponse = await request(harness, 'DELETE', `/run_session/${firstRunId}`);
        await client.waitForNotification(notification => notification.session_id === firstRunId);

        const postTerminalFirstLog: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: firstRunId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'post-terminal first log',
        };
        const secondLog: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: secondRunId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'second run log',
        };
        const secondTerminated: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: secondRunId,
            dcp_id: harness.dcpId,
            exit_code: 0,
        };
        const firstTerminated: SessionTerminatedNotification = {
            ...secondTerminated,
            session_id: firstRunId,
        };
        firstHandler(postTerminalFirstLog);
        firstHandler(firstTerminated);
        secondHandler(secondLog);
        secondHandler(secondTerminated);
        await drainNotifications(client);

        assert.strictEqual(firstDeleteResponse.statusCode, 200);
        assert.strictEqual(firstStopSession.calledOnce, true);
        assert.strictEqual(secondStopSession.called, false);
        assert.deepStrictEqual(client.notifications, [
            {
                notification_type: 'sessionTerminated',
                session_id: firstRunId,
            },
            {
                notification_type: 'serviceLogs',
                session_id: secondRunId,
                is_std_err: false,
                log_message: 'second run log',
            },
            {
                notification_type: 'sessionTerminated',
                session_id: secondRunId,
                exit_code: 0,
            },
        ]);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.size, 2);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.size, 0);

        const completedRunDeleteResponse = await request(harness, 'DELETE', `/run_session/${secondRunId}`);
        assert.strictEqual(completedRunDeleteResponse.statusCode, 200);
    });

    test('dispose clears run state and prevents captured callbacks from refilling queues', async () => {
        const client = await openNotificationClient(harness);
        const createResponse = await createRunSession(harness);
        const runLocation = createResponse.headers.location;
        assert.ok(runLocation);
        const runId = runLocation.substring(runLocation.lastIndexOf('/') + 1);
        const adapterNotificationHandler = harness.dcpServer.createRunSessionNotificationHandler(runId);
        assert.ok(adapterNotificationHandler);
        await closeNotificationClient(harness, client);

        const log: ServiceLogsNotification = {
            notification_type: 'serviceLogs',
            session_id: runId,
            dcp_id: harness.dcpId,
            is_std_err: false,
            log_message: 'queued before dispose',
        };
        adapterNotificationHandler(log);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.size, 1);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.size, 1);
        assert.strictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.size, 1);

        const server = getInternals(harness.dcpServer).server;
        const serverClosed = once(server, 'close');
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 1);
        harness.dcpServer.dispose();
        harness.dcpServer.dispose();
        harness.disposed = true;
        await serverClosed;

        adapterNotificationHandler(log);
        const terminated: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 9,
        };
        adapterNotificationHandler(terminated);

        assert.strictEqual(getInternals(harness.dcpServer)._runSessions.size, 0);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.size, 0);
        assert.strictEqual(getInternals(harness.dcpServer).pendingNotificationsByRoutingKey.size, 0);
        assert.strictEqual(getInternals(harness.dcpServer).wsByRoutingKey.size, 0);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions._retentionTimers.size, 0);
        const runSessionEndEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(runSessionEndEvents.length, 1);
        assert.deepStrictEqual(runSessionEndEvents[0].properties, {
            resource_type: 'node',
            mode: 'Debug',
            exit_code_bucket: 'canceled',
        });
        assert.strictEqual(runSessionEndEvents[0].measurements?.exit_code, -1);
        assert.strictEqual(typeof runSessionEndEvents[0].measurements?.duration_ms, 'number');
        assert.strictEqual(
            telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/start').length,
            runSessionEndEvents.length);
    });
});

function createAspireDebugSessionForRunSessionTests(dcpServer: AspireDcpServer): AspireDebugSession {
    const parentDebugSession = {
        id: 'aspire-session',
        type: 'aspire',
        name: 'Aspire',
        workspaceFolder: undefined,
        configuration: {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/AppHost/AppHost.csproj',
        },
        customRequest: sinon.stub(),
        getDebugProtocolBreakpoint: sinon.stub(),
    };
    const terminalProvider = {
        isDebugConfigEnvironmentLoggingEnabled: () => false,
    };

    return new AspireDebugSession(
        parentDebugSession as unknown as vscode.DebugSession,
        {} as any,
        dcpServer,
        terminalProvider as any,
        () => { });
}

async function startHarness(options?: DcpServerOptions): Promise<Harness> {
    const dcpSessionId = 'aspire-extension-run-test';
    const dcpId = `${dcpSessionId}-resource`;
    const stopDebugging = sinon.stub().resolves();
    const queuedStopDebugging: sinon.SinonStub[] = [];
    const startAndGetDebugSession = sinon.stub().callsFake(() => {
        const resourceStopDebugging = queuedStopDebugging.shift() ?? stopDebugging;
        const resourceDebugSession: AspireResourceDebugSession = {
            id: `resource-debug-session-${resourceStopDebugging.callCount}`,
            session: {} as AspireResourceDebugSession['session'],
            stopSession: createMemoizedStopSession(resourceStopDebugging),
        };

        return Promise.resolve(resourceDebugSession);
    });
    const debugSession = {
        configuration: {},
        startAndGetDebugSession,
    } as unknown as AspireDebugSession;
    const dcpServer = await AspireDcpServer.create(debugSessionId => debugSessionId === dcpSessionId ? debugSession : null, {}, options);

    return {
        dcpId,
        dcpSessionId,
        dcpServer,
        disposed: false,
        queuedStopDebugging,
        sockets: [],
        startAndGetDebugSession,
        stopDebugging,
    };
}

async function stopHarness(harness: Harness): Promise<void> {
    const socketsClosed = harness.sockets
        .filter(socket => socket.readyState !== WebSocket.CLOSED)
        .map(closeHarnessSocket);

    const server = getInternals(harness.dcpServer).server;
    const closed = server.listening ? once(server, 'close') : Promise.resolve();
    if (!harness.disposed) {
        harness.dcpServer.dispose();
    }
    await Promise.all([closed, ...socketsClosed]);
}

async function closeHarnessSocket(socket: WebSocket): Promise<void> {
    if (socket.readyState === WebSocket.CONNECTING) {
        await new Promise<void>(resolve => {
            const settled = () => {
                socket.off('open', settled);
                socket.off('error', settled);
                socket.off('close', settled);
                resolve();
            };
            socket.once('open', settled);
            socket.once('error', settled);
            socket.once('close', settled);
        });
    }

    if (socket.readyState === WebSocket.CLOSED) {
        return;
    }

    const closed = once(socket, 'close');
    socket.close();
    await closed;
}

async function createRunSession(harness: Harness, stopDebugging?: sinon.SinonStub): Promise<HttpResponse> {
    const launchConfiguration: NodeLaunchConfiguration = {
        type: 'node',
        mode: 'Debug',
        script_path: __filename,
        working_directory: __dirname,
    };
    const payload: RunSessionPayload = {
        launch_configurations: [launchConfiguration],
    };
    if (stopDebugging) {
        harness.queuedStopDebugging.push(stopDebugging);
    }

    return await request(harness, 'PUT', '/run_session', payload);
}

async function openNotificationClient(harness: Harness, dcpId = harness.dcpId): Promise<NotificationClient> {
    const notifications: WireNotification[] = [];
    const waiters: {
        predicate: (notification: WireNotification) => boolean;
        resolve: (notification: WireNotification) => void;
    }[] = [];
    const socket = new WebSocket(`wss://${harness.dcpServer.connectionInfo.address}/run_session/notify`, {
        rejectUnauthorized: false,
        headers: getHeaders(harness, dcpId),
    });
    harness.sockets.push(socket);
    socket.on('message', data => {
        for (const line of data.toString().split('\n').filter(Boolean)) {
            const notification = JSON.parse(line) as WireNotification;
            notifications.push(notification);
            const waiterIndex = waiters.findIndex(waiter => waiter.predicate(notification));
            if (waiterIndex >= 0) {
                waiters.splice(waiterIndex, 1)[0].resolve(notification);
            }
        }
    });
    await once(socket, 'open');

    return {
        notifications,
        socket,
        waitForNotification: (predicate = () => true) => {
            const notification = notifications.find(predicate);
            return notification
                ? Promise.resolve(notification)
                : new Promise(resolve => waiters.push({ predicate, resolve }));
        },
    };
}

async function closeNotificationClient(harness: Harness, client: NotificationClient, dcpId = harness.dcpId): Promise<void> {
    if (client.socket.readyState === WebSocket.CLOSED) {
        return;
    }

    const clientClosed = once(client.socket, 'close');
    const serverSocket = getInternals(harness.dcpServer).wsByRoutingKey.get(getSessionRoutingKey(dcpId));
    const serverClosed = serverSocket ? once(serverSocket, 'close') : Promise.resolve();
    client.socket.terminate();
    await Promise.all([clientClosed, serverClosed]);
}

async function drainNotifications(client: NotificationClient): Promise<void> {
    const pong = once(client.socket, 'pong');
    client.socket.ping();
    await pong;
}

async function request(harness: Harness, method: string, path: string, body?: unknown, dcpId = harness.dcpId): Promise<HttpResponse> {
    const [host, port] = harness.dcpServer.connectionInfo.address.split(':');
    const payload = body === undefined ? undefined : JSON.stringify(body);

    return await new Promise((resolve, reject) => {
        const request = https.request({
            host,
            port: Number(port),
            path,
            method,
            rejectUnauthorized: false,
            headers: {
                ...getHeaders(harness, dcpId),
                ...(payload === undefined ? {} : {
                    'Content-Type': 'application/json',
                    'Content-Length': Buffer.byteLength(payload),
                }),
            },
        }, response => {
            const chunks: Buffer[] = [];
            response.on('data', chunk => chunks.push(Buffer.from(chunk)));
            response.on('end', () => resolve({
                body: Buffer.concat(chunks).toString(),
                statusCode: response.statusCode,
                headers: response.headers,
            }));
        });
        request.on('error', reject);
        if (payload !== undefined) {
            request.write(payload);
        }
        request.end();
    });
}

function getHeaders(harness: Harness, dcpId = harness.dcpId): Record<string, string> {
    return {
        Authorization: `Bearer ${harness.dcpServer.connectionInfo.token}`,
        'Microsoft-Developer-DCP-Instance-ID': dcpId,
    };
}

function getInternals(dcpServer: AspireDcpServer): DcpServerInternals {
    return dcpServer as unknown as DcpServerInternals;
}

function createDeferred<T>(): {
    promise: Promise<T>;
    resolve: (value: T | PromiseLike<T>) => void;
    reject: (reason?: unknown) => void;
} {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((promiseResolve, promiseReject) => {
        resolve = promiseResolve;
        reject = promiseReject;
    });

    return { promise, resolve, reject };
}

function createMemoizedStopSession(stopDebugging: sinon.SinonStub): sinon.SinonStub {
    let stopSessionPromise: Promise<void> | undefined;
    return sinon.stub().callsFake(() => {
        if (stopSessionPromise) {
            return stopSessionPromise;
        }

        stopSessionPromise = stopDebugging();
        return stopSessionPromise;
    });
}

async function waitFor(predicate: () => boolean, description = 'condition', timeoutMs = 5_000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (!predicate()) {
        if (Date.now() >= deadline) {
            throw new Error(`Timed out after ${timeoutMs} ms waiting for ${description}.`);
        }
        await new Promise(resolve => setImmediate(resolve));
    }
}
