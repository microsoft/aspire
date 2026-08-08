import * as assert from 'assert';
import * as sinon from 'sinon';
import { RunSessionRegistry, TerminationTrigger } from '../dcp/RunSessionRegistry';
import { AspireResourceDebugSession, RunSessionNotification, ServiceLogsNotification } from '../dcp/types';

interface Recorded {
    exitCode: number | undefined;
    runId: string;
}

interface Sent {
    notification: RunSessionNotification;
    sessionPrefix: string;
}

const sessionPrefix = 'aspire-extension-run-abc123';

function createRegistry(retentionMs = 5_000): {
    completions: Recorded[];
    registry: RunSessionRegistry;
    sent: Sent[];
} {
    const completions: Recorded[] = [];
    const sent: Sent[] = [];
    const registry = new RunSessionRegistry({
        recordCompletion: (runId, exitCode) => completions.push({ runId, exitCode }),
        retentionMs,
        send: (prefix, notification) => sent.push({ sessionPrefix: prefix, notification }),
    });

    return { completions, registry, sent };
}

function register(registry: RunSessionRegistry, runId: string, terminationTrigger: TerminationTrigger): void {
    registry.register({
        debugSessions: [] as AspireResourceDebugSession[],
        runId,
        sessionPrefix,
        terminationTrigger,
    });
}

function createLog(runId: string, logMessage: string): ServiceLogsNotification {
    return {
        notification_type: 'serviceLogs',
        session_id: runId,
        dcp_id: `${sessionPrefix}-instance`,
        is_std_err: false,
        log_message: logMessage,
    };
}

suite('RunSessionRegistry', () => {
    teardown(() => {
        sinon.restore();
    });

    test('notifications are routed to the debug session prefix regardless of the reporting instance id', () => {
        const { registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'adapterExit' });

        // The adapter reports whichever DCP instance id it observed at launch. Routing uses
        // the run's own prefix so a changed instance id cannot misdirect the stream.
        registry.notify('run-1', {
            ...createLog('run-1', 'hello'),
            dcp_id: 'aspire-extension-run-someoneelse-instance',
        });

        assert.deepStrictEqual(sent, [{
            sessionPrefix,
            notification: {
                notification_type: 'serviceLogs',
                session_id: 'run-1',
                dcp_id: 'aspire-extension-run-someoneelse-instance',
                is_std_err: false,
                log_message: 'hello',
            },
        }]);
    });

    test('notifications for an unknown or mismatched run are dropped', () => {
        const { registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'adapterExit' });

        registry.notify('run-missing', createLog('run-missing', 'unknown run'));
        registry.notify('run-1', createLog('run-2', 'mismatched session id'));

        assert.deepStrictEqual(sent, []);
    });

    test('terminate sends exactly one terminal notification per run', () => {
        const { registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'adapterExit' });

        registry.terminate('run-1', 3);
        registry.terminate('run-1', 3);
        registry.notify('run-1', createLog('run-1', 'after termination'));

        assert.deepStrictEqual(sent, [{
            sessionPrefix,
            notification: {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: sessionPrefix,
                exit_code: 3,
            },
        }]);
    });

    test('requestStop terminates once and reports already-stopping runs to its caller', () => {
        const { registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'adapterExit' });

        assert.strictEqual(registry.requestStop('run-1'), true);
        assert.strictEqual(registry.requestStop('run-1'), false);
        assert.strictEqual(registry.requestStop('run-missing'), false);

        assert.deepStrictEqual(sent, [{
            sessionPrefix,
            notification: {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: sessionPrefix,
            },
        }]);
        assert.strictEqual(registry.get('run-1')?.lifecycle, 'stopRequested');
    });

    test('an adapterExit run keeps its record so a late adapter exit refines the recorded exit code', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true, toFake: ['setTimeout', 'clearTimeout'] });
        const { completions, registry, sent } = createRegistry(1_000);
        register(registry, 'run-1', { kind: 'adapterExit' });

        registry.requestStop('run-1');
        assert.deepStrictEqual(completions, []);

        registry.terminate('run-1', 17);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: 17 }]);
        assert.strictEqual(sent.length, 1);

        await clock.tickAsync(1_000);
        assert.strictEqual(registry.get('run-1'), undefined);
    });

    test('an adapterExit run with no adapter exit is recorded as canceled at the retention deadline', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true, toFake: ['setTimeout', 'clearTimeout'] });
        const { completions, registry } = createRegistry(1_000);
        register(registry, 'run-1', { kind: 'adapterExit' });

        registry.requestStop('run-1');
        await clock.tickAsync(1_000);

        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);
        assert.strictEqual(registry.get('run-1'), undefined);
    });

    test('a debugSessionEnd run terminates on the debug session end signal and is released immediately', () => {
        const { completions, registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'debugSessionEnd' });

        // No adapter exit will ever arrive for this run, so the signal that does arrive is
        // final and nothing is held for a later exit code.
        registry.terminate('run-1', undefined);

        assert.deepStrictEqual(sent, [{
            sessionPrefix,
            notification: {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: sessionPrefix,
            },
        }]);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);
        assert.strictEqual(registry.get('run-1'), undefined);

        // A duplicate signal for the same debug session cannot produce a second notification,
        // so callers do not need to track which runs they have already terminated.
        registry.terminate('run-1', undefined);
        assert.strictEqual(sent.length, 1);
    });

    test('a requestOnly run is released as soon as the stop is requested', () => {
        const { completions, registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'requestOnly' });

        assert.strictEqual(registry.requestStop('run-1'), true);

        assert.strictEqual(sent.length, 1);
        assert.deepStrictEqual(completions, [{ runId: 'run-1', exitCode: -1 }]);
        // A retried DELETE for this run now finds nothing, which the server answers with
        // 204 No Content per docs/specs/IDE-execution.md.
        assert.strictEqual(registry.get('run-1'), undefined);
        assert.strictEqual(registry.requestStop('run-1'), false);
    });

    test('remove drops a run that never started without terminating it', () => {
        const { completions, registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'adapterExit' });

        registry.remove('run-1');
        registry.notify('run-1', createLog('run-1', 'after removal'));

        assert.deepStrictEqual(sent, []);
        assert.deepStrictEqual(completions, []);
        assert.strictEqual(registry.size, 0);
    });

    test('dispose closes every registered run once and stops further work', () => {
        const { completions, registry, sent } = createRegistry();
        register(registry, 'run-1', { kind: 'adapterExit' });
        register(registry, 'run-2', { kind: 'adapterExit' });

        registry.dispose();
        registry.dispose();

        assert.deepStrictEqual(completions, [
            { runId: 'run-1', exitCode: -1 },
            { runId: 'run-2', exitCode: -1 },
        ]);
        assert.deepStrictEqual(sent, []);
        assert.strictEqual(registry.size, 0);

        registry.notify('run-1', createLog('run-1', 'after dispose'));
        registry.terminate('run-2', 0);
        assert.deepStrictEqual(sent, []);
        assert.strictEqual(completions.length, 2);
    });
});
