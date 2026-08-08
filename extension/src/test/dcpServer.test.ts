import * as assert from 'assert';
import * as sinon from 'sinon';
import { stopRunSession } from '../dcp/AspireDcpServer';
import { AspireResourceDebugSession } from '../dcp/types';
import { createDebugSession } from './helpers/debugSessionHarness';

suite('Aspire DCP Server Tests', () => {
    teardown(() => {
        sinon.restore();
    });

    test('retains a run session when stopping one of its debug sessions fails', async () => {
        const runId = 'run-1';
        const failingDebugSession = createResourceDebugSession('resource-session-1', sinon.stub().rejects(new Error('VS Code failed to stop')));
        const runSessions = [failingDebugSession];
        const runsBySession = new Map<string, AspireResourceDebugSession[]>([
            [runId, runSessions]
        ]);

        const result = await stopRunSession(runId, runsBySession);

        assert.strictEqual(result.status, 'failed');
        assert.strictEqual(runsBySession.get(runId), runSessions, 'Expected the failed run entry to stay retryable');
        assert.strictEqual(runsBySession.get(runId)?.[0], failingDebugSession);
        assert.strictEqual(failingDebugSession.stopSession.calledOnce, true);
    });

    test('removes a run session only after all debug sessions stop successfully', async () => {
        const runId = 'run-1';
        const firstDebugSession = createResourceDebugSession('resource-session-1', sinon.stub().resolves());
        const secondDebugSession = createResourceDebugSession('resource-session-2', sinon.stub().resolves());
        const runsBySession = new Map<string, AspireResourceDebugSession[]>([
            [runId, [firstDebugSession, secondDebugSession]]
        ]);

        const result = await stopRunSession(runId, runsBySession);

        assert.strictEqual(result.status, 'stopped');
        assert.strictEqual(runsBySession.has(runId), false);
        assert.strictEqual(firstDebugSession.stopSession.calledOnce, true);
        assert.strictEqual(secondDebugSession.stopSession.calledOnce, true);
    });
});

type StopSessionStub = sinon.SinonStub;

function createResourceDebugSession(id: string, stopSession: StopSessionStub): AspireResourceDebugSession & { stopSession: StopSessionStub } {
    return {
        id,
        session: createDebugSession(id, {
            type: 'coreclr',
            name: id,
            request: 'launch',
        }),
        stopSession,
    };
}
