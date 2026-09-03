import * as assert from 'assert';
import * as sinon from 'sinon';
import { RunSessionRegistry } from '../dcp/RunSessionRegistry';
import type { AspireResourceDebugSession, RunSessionNotification, ServiceLogsNotification, SessionTerminatedNotification } from '../dcp/types';

interface Completion {
    exitCode: number;
    runId: string;
}

interface Delivery {
    notification: RunSessionNotification;
    ownerDcpId: string;
}

suite('RunSessionRegistry', () => {
    teardown(() => {
        sinon.restore();
    });

    test('requested adapter stop retains bounded state for a late exit without a second wire terminal', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true, toFake: ['setTimeout', 'clearTimeout'] });
        const { completions, deliveries, registry } = createRegistry(1_000);
        registerAdapterRun(registry, 'run-1');

        assert.strictEqual(registry.requestStop('run-1'), true);
        registry.notify({
            notification_type: 'serviceLogs',
            session_id: 'run-1',
            dcp_id: 'wrong-owner',
            is_std_err: false,
            log_message: 'after terminal',
        } as ServiceLogsNotification);
        registry.notify({
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'wrong-owner',
            exit_code: 17,
        } as SessionTerminatedNotification);

        assert.deepStrictEqual(deliveries, [{
            ownerDcpId: 'aspire-extension-run-owner-instance',
            notification: {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: 'aspire-extension-run-owner-instance',
            },
        }]);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: 17 }]);
        assert.strictEqual(registry.size, 1);

        await clock.tickAsync(1_000);

        assert.strictEqual(registry.size, 0);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: 17 }]);
    });

    test('terminal notification without an exit code omits it on the wire and records canceled telemetry', () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true, toFake: ['setTimeout', 'clearTimeout'] });
        const { completions, deliveries, registry } = createRegistry(1_000);
        registerAdapterRun(registry, 'run-1');

        registry.notify({
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'aspire-extension-run-owner-instance',
        } as SessionTerminatedNotification);

        assert.deepStrictEqual(deliveries, [{
            ownerDcpId: 'aspire-extension-run-owner-instance',
            notification: {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: 'aspire-extension-run-owner-instance',
            },
        }]);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);

        registry.dispose();
        assert.strictEqual(clock.countTimers(), 0);
    });

    test('synchronous terminal delivery failure remains retryable until confirmed stop succeeds', () => {
        const completions: Completion[] = [];
        const deliveries: Delivery[] = [];
        const deliveryError = new Error('terminal delivery failed');
        const send = sinon.stub();
        send.onFirstCall().throws(deliveryError);
        send.onSecondCall().callsFake((ownerDcpId: string, notification: RunSessionNotification) => {
            deliveries.push({ ownerDcpId, notification });
        });
        const registry = new RunSessionRegistry({
            recordCompletion: (runId: string, exitCode: number) => completions.push({ runId, exitCode }),
            retentionMs: 1_000,
            scheduleTeardown: () => undefined,
            send,
        });
        registry.register({
            debugSessions: [],
            kind: 'confirmedStop',
            ownerDcpId: 'aspire-extension-run-owner-instance',
            runId: 'run-1',
            startupCompletion: Promise.resolve(),
        });
        registry.markRunning('run-1');

        assert.throws(() => registry.terminate('run-1', 0), deliveryError);
        assert.strictEqual(registry.confirmStop('run-1'), true);

        assert.strictEqual(send.callCount, 2);
        assert.deepStrictEqual(deliveries, [{
            ownerDcpId: 'aspire-extension-run-owner-instance',
            notification: {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: 'aspire-extension-run-owner-instance',
            },
        }]);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);
        assert.strictEqual(registry.size, 0);
        assert.strictEqual(registry.confirmStop('run-1'), false);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);
    });

    test('retention expiry closes telemetry as canceled and evicts the run', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true, toFake: ['setTimeout', 'clearTimeout'] });
        const { completions, registry } = createRegistry(1_000);
        registerAdapterRun(registry, 'run-1');

        registry.requestStop('run-1');
        await clock.tickAsync(1_000);

        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);
        assert.strictEqual(registry.size, 0);
    });

    test('dispose cancels retention, closes incomplete telemetry once, and drops captured callbacks', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true, toFake: ['setTimeout', 'clearTimeout'] });
        const { completions, deliveries, registry } = createRegistry(1_000);
        registerAdapterRun(registry, 'run-1');
        registerAdapterRun(registry, 'run-2');
        registry.requestStop('run-1');
        registry.notify({
            notification_type: 'sessionTerminated',
            session_id: 'run-2',
            dcp_id: 'aspire-extension-run-owner-instance',
            exit_code: 0,
        } as SessionTerminatedNotification);

        registry.dispose();
        registry.dispose();
        await clock.tickAsync(1_000);
        registry.notify({
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'aspire-extension-run-owner-instance',
            exit_code: 9,
        } as SessionTerminatedNotification);

        assert.strictEqual(clock.countTimers(), 0);
        assert.strictEqual(registry.size, 0);
        assert.deepStrictEqual(completions, [
            { runId: 'run-2', exitCode: 0 },
            { runId: 'run-1', exitCode: -1 },
        ]);
        assert.strictEqual(deliveries.length, 2);
    });
});

function createRegistry(retentionMs: number): {
    completions: Completion[];
    deliveries: Delivery[];
    registry: RunSessionRegistry;
} {
    const completions: Completion[] = [];
    const deliveries: Delivery[] = [];
    const registry = new RunSessionRegistry({
        recordCompletion: (runId: string, exitCode: number) => completions.push({ runId, exitCode }),
        retentionMs,
        scheduleTeardown: () => undefined,
        send: (ownerDcpId: string, notification: RunSessionNotification) => deliveries.push({ ownerDcpId, notification }),
    });
    return { completions, deliveries, registry };
}

function registerAdapterRun(registry: RunSessionRegistry, runId: string): void {
    registry.register({
        debugSessions: [] as AspireResourceDebugSession[],
        kind: 'adapter',
        ownerDcpId: 'aspire-extension-run-owner-instance',
        runId,
        startupCompletion: Promise.resolve(),
    });
    registry.markRunning(runId);
}
