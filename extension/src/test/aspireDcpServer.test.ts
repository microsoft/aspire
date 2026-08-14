import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { once } from 'events';
import type { IncomingHttpHeaders } from 'http';
import * as https from 'https';
import * as sinon from 'sinon';
import WebSocket from 'ws';
import type { AspireDebugSession } from '../debugger/AspireDebugSession';
import * as debuggerExtensions from '../debugger/debuggerExtensions';
import { cleanupRun, registerRunCleanup } from '../debugger/runCleanupRegistry';
import AspireDcpServer from '../dcp/AspireDcpServer';
import type {
    AspireResourceDebugSession,
    BrowserLaunchConfiguration,
    NodeLaunchConfiguration,
    ProcessRestartedNotification,
    RunSessionNotification,
    RunSessionPayload,
    ServiceLogsNotification,
    SessionTerminatedNotification,
} from '../dcp/types';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';

interface DcpServerInternals {
    _runSessions?: {
        get(runId: string): {
            debugSessions: AspireResourceDebugSession[];
            lifecycle: string;
            teardownStarted: boolean;
        } | undefined;
        size: number;
    };
    _runTelemetryById: Map<string, unknown>;
    pendingNotificationQueueByDcpId: Map<string, RunSessionNotification[]>;
    server: https.Server;
    wsBySession: Map<string, WebSocket>;
}

interface DcpServerOptions {
    debuggerStopTimeoutMs?: number;
    runRetentionMs?: number;
}

interface Harness {
    dcpId: string;
    dcpServer: AspireDcpServer;
    queuedSessions: AspireResourceDebugSession[];
    sockets: WebSocket[];
    beginPendingDebugSessionStart: sinon.SinonStub;
    startDebugSession: sinon.SinonStub;
    trackAlreadyStartedSession: sinon.SinonStub;
}

interface HttpResponse {
    body: string;
    headers: IncomingHttpHeaders;
    statusCode: number | undefined;
}

interface WireNotification {
    exit_code?: number;
    is_std_err?: boolean;
    log_message?: string;
    notification_type: 'processRestarted' | 'serviceLogs' | 'sessionTerminated';
    pid?: number;
    session_id: string;
}

interface NotificationClient {
    notifications: WireNotification[];
    socket: WebSocket;
    waitForNotification(predicate?: (notification: WireNotification) => boolean): Promise<WireNotification>;
}

interface RecordedEvent {
    isError?: boolean;
    measurements?: Record<string, number>;
    name: string;
    properties?: Record<string, string>;
}

class FakeTelemetryReporter {
    public readonly events: RecordedEvent[] = [];
    public telemetryLevel: 'all' | 'error' | 'crash' | 'off' = 'all';

    sendTelemetryEvent(): void { }
    sendTelemetryErrorEvent(): void { }
    sendRawTelemetryEvent(): void { }
    dispose(): Promise<void> { return Promise.resolve(); }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true });
    }
}

suite('Aspire DCP run session lifecycle', () => {
    let harness: Harness;
    let telemetryReporter: FakeTelemetryReporter;
    let restoreTelemetry: () => void;

    setup(async () => {
        telemetryReporter = new FakeTelemetryReporter();
        restoreTelemetry = __setReporterForTests(telemetryReporter as unknown as TelemetryReporter);
        harness = await startHarness();
    });

    teardown(async () => {
        sinon.restore();
        restoreTelemetry();
        __resetCommonPropertiesForTests();
        await stopHarness(harness);
    });

    test('process DELETE terminates before blocked teardown and suppresses all later notifications', async () => {
        const stopSession = sinon.stub().returns(new Promise<void>(() => { }));
        const client = await openNotificationClient(harness);
        const runId = await createRun(harness, 'node', stopSession);
        const sendCore = sinon.spy(AspireDcpServer, 'sendNotificationCore');

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const terminal = await client.waitForNotification();

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(terminal, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.strictEqual(stopSession.calledOnce, true);
        assert.strictEqual(sendCore.calledBefore(stopSession), true);

        const lateNotifications: (ProcessRestartedNotification | ServiceLogsNotification | SessionTerminatedNotification)[] = [
            {
                notification_type: 'serviceLogs',
                session_id: runId,
                dcp_id: harness.dcpId,
                is_std_err: false,
                log_message: 'after terminal',
            } satisfies ServiceLogsNotification,
            {
                notification_type: 'processRestarted',
                session_id: runId,
                dcp_id: harness.dcpId,
                pid: 42,
            } satisfies ProcessRestartedNotification,
            {
                notification_type: 'sessionTerminated',
                session_id: runId,
                dcp_id: harness.dcpId,
                exit_code: 17,
            } satisfies SessionTerminatedNotification,
        ];
        lateNotifications.forEach(notification => harness.dcpServer.sendNotification(notification));
        const duplicateDelete = await request(harness, 'DELETE', `/run_session/${runId}`);
        await drainNotifications(client);

        assert.strictEqual(duplicateDelete.statusCode, 200);
        assert.deepStrictEqual(client.notifications, [terminal]);
        assert.strictEqual(stopSession.calledOnce, true);
    });

    test('late adapter exit refines telemetry without sending a second terminal notification', async () => {
        const client = await openNotificationClient(harness);
        const runId = await createRun(harness, 'node', sinon.stub().resolves());

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const terminal = await client.waitForNotification();
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(
            telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length,
            0);

        harness.dcpServer.sendNotification({
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 17,
        } as SessionTerminatedNotification);
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [terminal]);
        const endEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(endEvents.length, 1);
        assert.deepStrictEqual(endEvents[0].properties, {
            resource_type: 'node',
            mode: 'Debug',
            exit_code_bucket: 'nonzero',
        });
        assert.strictEqual(endEvents[0].measurements?.exit_code, 17);
        assert.strictEqual(endEvents[0].isError, true);
        assert.deepStrictEqual(harness.dcpServer.takeDebugSessionAggregateStats('aspire-extension-run-test'), {
            totalChildSessions: 1,
            distinctResourceTypes: ['node'],
            anyNonZeroExit: true,
        });
    });

    test('DELETE after a natural adapter exit schedules debugger teardown once', async () => {
        let runId = '';
        let cleanupCalls = 0;
        const stopCompleted = createDeferred<void>();
        const cleanupCompleted = createDeferred<void>();
        const stopSession = sinon.stub().callsFake(async () => {
            await stopCompleted.promise;
            cleanupRun(runId);
        });
        const client = await openNotificationClient(harness);
        runId = await createRun(harness, 'node', stopSession);
        registerRunCleanup(runId, () => {
            cleanupCalls++;
            cleanupCompleted.resolve();
        });
        const clock = sinon.useFakeTimers({ toFake: ['setImmediate', 'clearImmediate'] });

        try {
            harness.dcpServer.sendNotification({
                notification_type: 'sessionTerminated',
                session_id: runId,
                dcp_id: harness.dcpId,
                exit_code: 0,
            } as SessionTerminatedNotification);
            const terminal = await client.waitForNotification();

            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            const repeatedDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            await clock.runAllAsync();

            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(deleteResponse.body, '');
            assert.strictEqual(repeatedDeleteResponse.statusCode, 200);
            assert.strictEqual(repeatedDeleteResponse.body, '');
            assert.strictEqual(stopSession.calledOnce, true);
            assert.strictEqual(getInternals(harness.dcpServer)._runSessions?.get(runId)?.teardownStarted, true);

            stopCompleted.resolve();
            await cleanupCompleted.promise;
            await drainNotifications(client);

            assert.strictEqual(cleanupCalls, 1);
            assert.deepStrictEqual(client.notifications, [terminal]);
            const endEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
            assert.strictEqual(endEvents.length, 1);
            assert.deepStrictEqual(endEvents[0].properties, {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'success',
            });
            assert.strictEqual(endEvents[0].measurements?.exit_code, 0);
            assert.strictEqual(endEvents[0].isError, undefined);
        } finally {
            stopCompleted.resolve();
            cleanupRun(runId);
        }
    });

    test('natural adapter exit teardown survives retention expiry and delayed DELETE', async () => {
        await stopHarness(harness);
        harness = await startHarness({ runRetentionMs: 1 });
        let runId = '';
        let cleanupCalls = 0;
        const stopCompleted = createDeferred<void>();
        const cleanupCompleted = createDeferred<void>();
        const stopSession = sinon.stub().callsFake(async () => {
            await stopCompleted.promise;
            cleanupRun(runId);
        });
        const client = await openNotificationClient(harness);
        runId = await createRun(harness, 'node', stopSession);
        registerRunCleanup(runId, () => {
            cleanupCalls++;
            cleanupCompleted.resolve();
        });
        const clock = sinon.useFakeTimers({
            toFake: ['setImmediate', 'clearImmediate', 'setTimeout', 'clearTimeout'],
        });

        try {
            harness.dcpServer.sendNotification({
                notification_type: 'sessionTerminated',
                session_id: runId,
                dcp_id: harness.dcpId,
                exit_code: 0,
            } as SessionTerminatedNotification);
            const terminal = await client.waitForNotification();
            await clock.runAllAsync();

            assert.strictEqual(getInternals(harness.dcpServer)._runSessions?.get(runId), undefined);

            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            const repeatedDeleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);

            assert.strictEqual(deleteResponse.statusCode, 204);
            assert.strictEqual(deleteResponse.body, '');
            assert.strictEqual(repeatedDeleteResponse.statusCode, 204);
            assert.strictEqual(repeatedDeleteResponse.body, '');
            assert.strictEqual(stopSession.calledOnce, true);

            stopCompleted.resolve();
            await cleanupCompleted.promise;
            await drainNotifications(client);

            assert.strictEqual(cleanupCalls, 1);
            assert.deepStrictEqual(client.notifications, [terminal]);
            const endEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
            assert.strictEqual(endEvents.length, 1);
            assert.deepStrictEqual(endEvents[0].properties, {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'success',
            });
            assert.strictEqual(endEvents[0].measurements?.exit_code, 0);
            assert.strictEqual(endEvents[0].isError, undefined);
        } finally {
            stopCompleted.resolve();
            cleanupRun(runId);
        }
    });

    test('launch failure uses the terminal deduper', async () => {
        sinon.stub(debuggerExtensions, 'prepareDebugSession').throws(new Error('launch failed'));
        const client = await openNotificationClient(harness);

        const createResponse = await createRunResponse(harness, 'node', sinon.stub().resolves());
        const terminal = await client.waitForNotification();
        const runId = terminal.session_id;
        harness.dcpServer.sendNotification({
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: harness.dcpId,
            exit_code: 9,
        } as SessionTerminatedNotification);
        await drainNotifications(client);

        assert.strictEqual(createResponse.statusCode, 500);
        assert.deepStrictEqual(client.notifications, [{
            notification_type: 'sessionTerminated',
            session_id: runId,
        }]);
        assert.deepStrictEqual(harness.dcpServer.takeDebugSessionAggregateStats('aspire-extension-run-test'), {
            totalChildSessions: 1,
            distinctResourceTypes: ['node'],
            anyNonZeroExit: true,
        });
    });

    test('debugger did not start terminates exactly once', async () => {
        harness.startDebugSession.resetBehavior();
        harness.startDebugSession.resolves(undefined);
        const client = await openNotificationClient(harness);

        const createResponse = await createRunResponse(harness, 'node', sinon.stub().resolves());
        await drainNotifications(client);

        assert.strictEqual(createResponse.statusCode, 500);
        assert.strictEqual(client.notifications.length, 1);
        const terminal = client.notifications[0];
        assert.deepStrictEqual(terminal, {
            notification_type: 'sessionTerminated',
            session_id: terminal.session_id,
        });

        harness.dcpServer.sendNotification({
            notification_type: 'sessionTerminated',
            session_id: terminal.session_id,
            dcp_id: harness.dcpId,
            exit_code: 9,
        } as SessionTerminatedNotification);
        await drainNotifications(client);

        assert.deepStrictEqual(client.notifications, [terminal]);
        const endEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(endEvents.length, 1);
        assert.strictEqual(endEvents[0].properties?.end_reason, 'debugger_did_not_start');
    });

    test('process teardown observes synchronous throws and rejections from every debug session', async () => {
        const unhandled: unknown[] = [];
        const onUnhandled = (reason: unknown) => unhandled.push(reason);
        process.on('unhandledRejection', onUnhandled);
        try {
            const firstStop = sinon.stub().throws(new Error('sync stop failure'));
            const secondStop = sinon.stub().rejects(new Error('async stop failure'));
            const thirdStop = sinon.stub().resolves();
            const runId = await createRun(harness, 'node', firstStop);
            const run = getInternals(harness.dcpServer)._runSessions?.get(runId);
            assert.ok(run, 'expected bounded run-session state');
            run.debugSessions.push(createResourceSession('second', secondStop), createResourceSession('third', thirdStop));

            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            await new Promise(resolve => setImmediate(resolve));
            await new Promise(resolve => setImmediate(resolve));

            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(firstStop.calledOnce, true);
            assert.strictEqual(secondStop.calledOnce, true);
            assert.strictEqual(thirdStop.calledOnce, true);
            assert.deepStrictEqual(unhandled, []);
        } finally {
            process.off('unhandledRejection', onUnhandled);
        }
    });

    test('DELETE while debugger start is pending stops the late session exactly once', async () => {
        const startCompleted = createDeferred<AspireResourceDebugSession>();
        const stopSession = sinon.stub().resolves();
        harness.startDebugSession.resetBehavior();
        harness.startDebugSession.returns(startCompleted.promise);
        const client = await openNotificationClient(harness);
        const createPromise = createRunResponse(harness, 'node', stopSession);
        await waitFor(() => getInternals(harness.dcpServer)._runTelemetryById.size === 1);
        const runId = getInternals(harness.dcpServer)._runTelemetryById.keys().next().value;
        assert.ok(runId);
        const clock = sinon.useFakeTimers({ toFake: ['setImmediate', 'clearImmediate'] });

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const terminal = await client.waitForNotification();
        startCompleted.resolve(createResourceSession('late-node-session', stopSession));
        const createResponse = await createPromise;
        await clock.runAllAsync();
        await drainNotifications(client);

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(createResponse.statusCode, 409);
        assert.strictEqual(stopSession.calledOnce, true);
        assert.deepStrictEqual(client.notifications, [terminal]);
        assert.strictEqual(
            telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end').length,
            0);
    });

    test('DELETE during pending debugger start treats undefined as cancellation', async () => {
        await stopHarness(harness);
        harness = await startHarness({ runRetentionMs: 1 });
        const startCompleted = createDeferred<AspireResourceDebugSession | undefined>();
        const startInvoked = createDeferred<string>();
        const stopSession = sinon.stub().resolves();
        harness.startDebugSession.resetBehavior();
        harness.startDebugSession.callsFake((configuration: { runId: string }) => {
            startInvoked.resolve(configuration.runId);
            return startCompleted.promise;
        });
        const client = await openNotificationClient(harness);
        const createPromise = createRunResponse(harness, 'node', stopSession);
        const runId = await startInvoked.promise;
        let cleanupCalls = 0;
        registerRunCleanup(runId, () => cleanupCalls++);
        const clock = sinon.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });

        try {
            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            const terminal = await client.waitForNotification();
            await clock.tickAsync(1);
            startCompleted.resolve(undefined);
            const createResponse = await createPromise;
            await drainNotifications(client);

            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(deleteResponse.body, '');
            assert.strictEqual(createResponse.statusCode, 409);
            assert.deepStrictEqual(JSON.parse(createResponse.body), {
                error: {
                    code: 'RunSessionTerminated',
                    message: `Run session ${runId} terminated while its debug session was starting.`,
                    details: [],
                },
            });
            assert.strictEqual(harness.startDebugSession.calledOnce, true);
            assert.strictEqual(stopSession.notCalled, true);
            assert.strictEqual(cleanupCalls, 1);
            assert.deepStrictEqual(client.notifications, [terminal]);
            assert.strictEqual(getInternals(harness.dcpServer)._runSessions?.get(runId), undefined);

            const endEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
            assert.strictEqual(endEvents.length, 1);
            assert.deepStrictEqual(endEvents[0].properties, {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'canceled',
            });
            assert.strictEqual(endEvents[0].measurements?.exit_code, -1);
            assert.strictEqual(endEvents[0].isError, undefined);
            assert.deepStrictEqual(harness.dcpServer.takeDebugSessionAggregateStats('aspire-extension-run-test'), {
                totalChildSessions: 1,
                distinctResourceTypes: ['node'],
                anyNonZeroExit: false,
            });
        } finally {
            startCompleted.resolve(undefined);
            cleanupRun(runId);
        }
    });

    test('DELETE while debugger preparation is pending does not launch and stops a callback-created session', async () => {
        const preparationCompleted = createDeferred<debuggerExtensions.PreparedDebugSession>();
        sinon.stub(debuggerExtensions, 'prepareDebugSession').returns(preparationCompleted.promise);
        const stopSession = sinon.stub().resolves();
        const createPromise = createRunResponse(harness, 'node', stopSession);
        await waitFor(() => getInternals(harness.dcpServer)._runTelemetryById.size === 1);
        const runId = getInternals(harness.dcpServer)._runTelemetryById.keys().next().value;
        assert.ok(runId);

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        preparationCompleted.resolve({
            debugConfiguration: {
                debugSessionId: harness.dcpId,
                name: 'prepared node session',
                request: 'launch',
                runId,
                type: 'node',
            },
            alreadyStartedSession: {
                ...createResourceSession('prepared-node-session', stopSession),
                processId: 1234,
                termination: new Promise<number>(() => { }),
            },
        });
        const createResponse = await createPromise;

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(createResponse.statusCode, 409);
        assert.strictEqual(harness.startDebugSession.notCalled, true);
        assert.strictEqual(harness.trackAlreadyStartedSession.notCalled, true);
        assert.strictEqual(stopSession.calledOnce, true);
    });

    test('debugger preparation remains pending shutdown work until the prepared session is handed off', async () => {
        const preparationCompleted = createDeferred<debuggerExtensions.PreparedDebugSession>();
        const prepareDebugSession = sinon.stub(debuggerExtensions, 'prepareDebugSession').returns(preparationCompleted.promise);
        const stopSession = sinon.stub().resolves();
        const createPromise = createRunResponse(harness, 'node', stopSession);

        await waitFor(() => harness.beginPendingDebugSessionStart.calledOnce);
        const pendingStart = harness.beginPendingDebugSessionStart.firstCall.returnValue;
        assert.strictEqual(harness.beginPendingDebugSessionStart.calledBefore(prepareDebugSession), true);
        assert.strictEqual(pendingStart.dispose.notCalled, true);
        const runId = getInternals(harness.dcpServer)._runTelemetryById.keys().next().value;
        assert.ok(runId);

        preparationCompleted.resolve({
            debugConfiguration: {
                debugSessionId: harness.dcpId,
                name: 'prepared node session',
                request: 'launch',
                runId,
                type: 'node',
            },
            alreadyStartedSession: {
                ...createResourceSession('prepared-node-session', stopSession),
                processId: 1234,
                termination: new Promise<number>(() => { }),
            },
        });
        const createResponse = await createPromise;

        assert.strictEqual(createResponse.statusCode, 201);
        sinon.assert.calledOnce(pendingStart.dispose);
        sinon.assert.calledOnce(harness.trackAlreadyStartedSession);
        assert.strictEqual(harness.trackAlreadyStartedSession.calledBefore(pendingStart.dispose), true);
    });

    test('DELETE during pending preparation treats a later rejection as cancellation', async () => {
        await stopHarness(harness);
        harness = await startHarness({ runRetentionMs: 1 });
        const preparationCompleted = createDeferred<debuggerExtensions.PreparedDebugSession>();
        const preparationInvoked = createDeferred<string>();
        sinon.stub(debuggerExtensions, 'prepareDebugSession').callsFake(
            (_debugSessionConfiguration, _launchConfiguration, _args, _env, launchOptions) => {
                preparationInvoked.resolve(launchOptions.runId);
                return preparationCompleted.promise;
            });
        const stopSession = sinon.stub().resolves();
        const client = await openNotificationClient(harness);
        const createPromise = createRunResponse(harness, 'node', stopSession);
        const runId = await preparationInvoked.promise;
        let cleanupCalls = 0;
        registerRunCleanup(runId, () => cleanupCalls++);
        const clock = sinon.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });

        try {
            const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
            const terminal = await client.waitForNotification();
            preparationCompleted.reject(new Error('preparation failed after DELETE'));
            const createResponse = await createPromise;
            await clock.tickAsync(1);
            await drainNotifications(client);

            assert.strictEqual(deleteResponse.statusCode, 200);
            assert.strictEqual(deleteResponse.body, '');
            assert.strictEqual(createResponse.statusCode, 409);
            assert.deepStrictEqual(JSON.parse(createResponse.body), {
                error: {
                    code: 'RunSessionTerminated',
                    message: `Run session ${runId} terminated while its debug session was starting.`,
                    details: [],
                },
            });
            assert.strictEqual(harness.startDebugSession.notCalled, true);
            assert.strictEqual(stopSession.notCalled, true);
            assert.strictEqual(cleanupCalls, 1);
            assert.deepStrictEqual(client.notifications, [terminal]);
            assert.strictEqual(getInternals(harness.dcpServer)._runSessions?.get(runId), undefined);

            const endEvents = telemetryReporter.events.filter(event => event.name === 'aspire/vscode/debug/runsession/end');
            assert.strictEqual(endEvents.length, 1);
            assert.deepStrictEqual(endEvents[0].properties, {
                resource_type: 'node',
                mode: 'Debug',
                exit_code_bucket: 'canceled',
            });
            assert.strictEqual(endEvents[0].measurements?.exit_code, -1);
            assert.strictEqual(endEvents[0].isError, undefined);
            assert.deepStrictEqual(harness.dcpServer.takeDebugSessionAggregateStats('aspire-extension-run-test'), {
                totalChildSessions: 1,
                distinctResourceTypes: ['node'],
                anyNonZeroExit: false,
            });
        } finally {
            cleanupRun(runId);
        }
    });

    test('browser DELETE waits for confirmed stop before terminating', async () => {
        const stopCompleted = createDeferred<void>();
        const stopSession = sinon.stub().returns(stopCompleted.promise);
        const client = await openNotificationClient(harness);
        const runId = await createRun(harness, 'browser', stopSession);

        const deletePromise = request(harness, 'DELETE', `/run_session/${runId}`);
        await waitFor(() => stopSession.calledOnce);
        await drainNotifications(client);
        assert.deepStrictEqual(client.notifications, []);

        stopCompleted.resolve();
        const deleteResponse = await deletePromise;
        const terminal = await client.waitForNotification();

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.deepStrictEqual(terminal, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        const duplicateDelete = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(duplicateDelete.statusCode, 204);
    });

    test('failed browser stop returns 500 without termination and can be retried', async () => {
        const stopSession = sinon.stub();
        stopSession.onFirstCall().throws(new Error('browser stop failed'));
        stopSession.onSecondCall().resolves();
        const client = await openNotificationClient(harness);
        const runId = await createRun(harness, 'browser', stopSession);

        const failedResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        await drainNotifications(client);
        assert.strictEqual(failedResponse.statusCode, 500);
        assert.deepStrictEqual(client.notifications, []);

        const retryResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const terminal = await client.waitForNotification();

        assert.strictEqual(retryResponse.statusCode, 200);
        assert.strictEqual(stopSession.callCount, 2);
        assert.deepStrictEqual(client.notifications, [terminal]);
    });

    test('timed-out browser stop returns 500 and a later retry can confirm termination', async () => {
        await stopHarness(harness);
        harness = await startHarness({ debuggerStopTimeoutMs: 25 });
        const stopCompleted = createDeferred<void>();
        const stopSession = sinon.stub().returns(stopCompleted.promise);
        const client = await openNotificationClient(harness);
        const runId = await createRun(harness, 'browser', stopSession);

        const timedOutResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        await drainNotifications(client);
        assert.strictEqual(timedOutResponse.statusCode, 500);
        assert.deepStrictEqual(client.notifications, []);

        stopCompleted.resolve();
        const retryResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const terminal = await client.waitForNotification();

        assert.strictEqual(retryResponse.statusCode, 200);
        assert.strictEqual(stopSession.calledOnce, true);
        assert.deepStrictEqual(client.notifications, [terminal]);
    });

    test('DELETE accepts a reconnected DCP instance with the same stable prefix', async () => {
        const originalClient = await openNotificationClient(harness);
        const stopSession = sinon.stub().resolves();
        const runId = await createRun(harness, 'node', stopSession);
        const reconnectedDcpId = 'aspire-extension-run-test-reconnected';
        const reconnectedClient = await openNotificationClient(harness, reconnectedDcpId);
        await waitFor(() => originalClient.socket.readyState === WebSocket.CLOSED, 1_000);

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`, undefined, reconnectedDcpId);

        assert.strictEqual(deleteResponse.statusCode, 200, deleteResponse.body);
        const terminal = await reconnectedClient.waitForNotification();
        assert.deepStrictEqual(reconnectedClient.notifications, [terminal]);
        assert.deepStrictEqual(originalClient.notifications, []);
        assert.strictEqual(stopSession.calledOnce, true);
    });

    test('disconnected DELETE queues one terminal notification for same-prefix reconnect', async () => {
        const originalClient = await openNotificationClient(harness);
        const stopSession = sinon.stub().resolves();
        const runId = await createRun(harness, 'node', stopSession);
        const routingDcpId = 'aspire-extension-run-test';
        const reconnectedDcpId = `${routingDcpId}-reconnected`;
        const originalClosed = once(originalClient.socket, 'close');
        originalClient.socket.close();
        await originalClosed;
        await waitFor(() => !getInternals(harness.dcpServer).wsBySession.has(routingDcpId));

        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`, undefined, reconnectedDcpId);

        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(deleteResponse.body, '');
        assert.deepStrictEqual(getInternals(harness.dcpServer).pendingNotificationQueueByDcpId.get(routingDcpId), [{
            notification_type: 'sessionTerminated',
            session_id: runId,
            dcp_id: routingDcpId,
        }]);

        const reconnectedClient = await openNotificationClient(harness, reconnectedDcpId);
        const terminal = await reconnectedClient.waitForNotification();
        await drainNotifications(reconnectedClient);

        assert.deepStrictEqual(terminal, {
            notification_type: 'sessionTerminated',
            session_id: runId,
        });
        assert.deepStrictEqual(reconnectedClient.notifications, [terminal]);
        assert.deepStrictEqual(originalClient.notifications, []);
        assert.strictEqual(getInternals(harness.dcpServer).pendingNotificationQueueByDcpId.has(routingDcpId), false);
        assert.strictEqual(stopSession.calledOnce, true);
    });

    test('DELETE rejects a DCP instance that does not own the run', async () => {
        const ownerClient = await openNotificationClient(harness);
        const intruderDcpId = 'aspire-extension-run-intruder-resource';
        const intruderClient = await openNotificationClient(harness, intruderDcpId);
        const stopSession = sinon.stub().resolves();
        const runId = await createRun(harness, 'node', stopSession);

        const intruderResponse = await request(harness, 'DELETE', `/run_session/${runId}`, undefined, intruderDcpId);
        await Promise.all([drainNotifications(ownerClient), drainNotifications(intruderClient)]);

        assert.strictEqual(intruderResponse.statusCode, 403);
        assert.strictEqual(JSON.parse(intruderResponse.body).error.code, 'RunSessionOwnerMismatch');
        assert.strictEqual(stopSession.called, false);
        assert.deepStrictEqual(ownerClient.notifications, []);
        assert.deepStrictEqual(intruderClient.notifications, []);

        const ownerResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        const terminal = await ownerClient.waitForNotification();
        assert.strictEqual(ownerResponse.statusCode, 200);
        assert.deepStrictEqual(ownerClient.notifications, [terminal]);
        assert.deepStrictEqual(intruderClient.notifications, []);
    });

    test('server dispose clears retained run lifecycle and telemetry state', async () => {
        const runId = await createRun(harness, 'node', sinon.stub().resolves());
        const deleteResponse = await request(harness, 'DELETE', `/run_session/${runId}`);
        assert.strictEqual(deleteResponse.statusCode, 200);
        assert.strictEqual(getInternals(harness.dcpServer)._runSessions?.size, 1);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.size, 1);

        const server = getInternals(harness.dcpServer).server;
        const serverClosed = once(server, 'close');
        harness.dcpServer.dispose();
        await serverClosed;

        assert.strictEqual(getInternals(harness.dcpServer)._runSessions?.size, 0);
        assert.strictEqual(getInternals(harness.dcpServer)._runTelemetryById.size, 0);
    });
});

async function startHarness(options?: DcpServerOptions): Promise<Harness> {
    const dcpSessionId = 'aspire-extension-run-test';
    const dcpId = `${dcpSessionId}-resource`;
    const queuedSessions: AspireResourceDebugSession[] = [];
    const beginPendingDebugSessionStart = sinon.stub().callsFake(() => ({ dispose: sinon.stub() }));
    const startDebugSession = sinon.stub().callsFake(async () => queuedSessions.shift());
    const trackAlreadyStartedSession = sinon.stub().callsFake(
        (_configuration: unknown, session: AspireResourceDebugSession) => session);
    const debugSession = {
        configuration: {},
        beginPendingDebugSessionStart,
        startAndGetDebugSession: startDebugSession,
        trackAlreadyStartedResourceSession: trackAlreadyStartedSession,
    } as unknown as AspireDebugSession;
    const create = AspireDcpServer.create as unknown as (
        getDebugSession: (debugSessionId: string) => AspireDebugSession | null,
        hooks?: {},
        options?: DcpServerOptions) => Promise<AspireDcpServer>;
    const dcpServer = await create(debugSessionId => debugSessionId === dcpSessionId ? debugSession : null, {}, options);

    return {
        dcpId,
        dcpServer,
        queuedSessions,
        sockets: [],
        beginPendingDebugSessionStart,
        startDebugSession,
        trackAlreadyStartedSession,
    };
}

async function stopHarness(harness: Harness): Promise<void> {
    const socketClosures = harness.sockets
        .filter(socket => socket.readyState !== WebSocket.CLOSED)
        .map(async socket => {
            const closed = once(socket, 'close');
            socket.close();
            await closed;
        });
    const server = getInternals(harness.dcpServer).server;
    const serverClosed = server.listening ? once(server, 'close') : Promise.resolve();
    harness.dcpServer.dispose();
    await Promise.all([serverClosed, ...socketClosures]);
}

async function createRun(harness: Harness, type: 'browser' | 'node', stopSession: sinon.SinonStub): Promise<string> {
    const response = await createRunResponse(harness, type, stopSession);
    assert.strictEqual(response.statusCode, 201, response.body);
    const location = response.headers.location;
    assert.ok(location);
    return location.substring(location.lastIndexOf('/') + 1);
}

async function createRunResponse(harness: Harness, type: 'browser' | 'node', stopSession: sinon.SinonStub): Promise<HttpResponse> {
    harness.queuedSessions.push(createResourceSession(`${type}-session`, stopSession));
    const launchConfiguration: BrowserLaunchConfiguration | NodeLaunchConfiguration = type === 'browser'
        ? {
            type: 'browser',
            mode: 'Debug',
            url: 'https://localhost:5001',
            browser: 'msedge',
        }
        : {
            type: 'node',
            mode: 'Debug',
            script_path: __filename,
            working_directory: __dirname,
        };
    const payload: RunSessionPayload = {
        launch_configurations: [launchConfiguration],
    };

    return await request(harness, 'PUT', '/run_session', payload);
}

function createResourceSession(id: string, stopSession: sinon.SinonStub): AspireResourceDebugSession {
    return {
        id,
        session: { id } as AspireResourceDebugSession['session'],
        stopSession,
    };
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
            const notification = JSON.parse(line) as WireNotification | { notification_type: 'connected' };
            if (notification.notification_type === 'connected') {
                continue;
            }
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
                headers: response.headers,
                statusCode: response.statusCode,
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
        Authorization: ['Bearer', harness.dcpServer.connectionInfo.token].join(' '),
        'Microsoft-Developer-DCP-Instance-ID': dcpId,
    };
}

function getInternals(dcpServer: AspireDcpServer): DcpServerInternals {
    return dcpServer as unknown as DcpServerInternals;
}

function createDeferred<T>(): {
    promise: Promise<T>;
    reject(reason?: unknown): void;
    resolve(value?: T | PromiseLike<T>): void;
} {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((promiseResolve, promiseReject) => {
        resolve = promiseResolve;
        reject = promiseReject;
    });
    return {
        promise,
        reject,
        resolve: resolve as (value?: T | PromiseLike<T>) => void,
    };
}

async function waitFor(predicate: () => boolean, timeoutMs = 5_000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (!predicate()) {
        if (Date.now() >= deadline) {
            throw new Error(`Timed out after ${timeoutMs} ms waiting for condition.`);
        }
        await new Promise(resolve => setImmediate(resolve));
    }
}
