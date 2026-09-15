import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';

import { TestRunSessionManager } from '../dcp/TestRunSessionManager';
import { DcpServerConnectionInfo } from '../dcp/types';
import { AspireDebugSession } from '../debugger/AspireDebugSession';

suite('TestRunSessionManager', () => {
    const connectionInfo: DcpServerConnectionInfo = {
        address: 'localhost:1234',
        token: 'test-token',
        certificate: 'test-cert',
    };

    teardown(() => {
        sinon.restore();
    });

    test('acquireTestRunSession returns DCP environment for a leased session', () => {
        const manager = new TestRunSessionManager(connectionInfo, () => ['project']);

        const lease = manager.acquireTestRunSession({ debug: true });

        assert.ok(lease.id);
        assert.ok(lease.sessionId.startsWith('aspire-extension-run-'));
        assert.strictEqual(lease.env.DEBUG_SESSION_PORT, connectionInfo.address);
        assert.strictEqual(lease.env.DEBUG_SESSION_TOKEN, connectionInfo.token);
        assert.strictEqual(lease.env.DEBUG_SESSION_SERVER_CERTIFICATE, connectionInfo.certificate);
        assert.strictEqual(lease.env.DCP_INSTANCE_ID_PREFIX, `${lease.sessionId}-`);
        assert.strictEqual(lease.env.DEBUG_SESSION_RUN_MODE, 'Debug');
        assert.deepStrictEqual(JSON.parse(lease.env.DEBUG_SESSION_INFO).supported_launch_configurations, ['project']);
    });

    test('releaseTestRunSession removes leased Aspire debug session', async () => {
        const debugSessionEvents = stubDebugSessionEvents();
        const manager = new TestRunSessionManager(connectionInfo);
        const addedSessions: AspireDebugSession[] = [];
        const removedSessions: AspireDebugSession[] = [];
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const lease = manager.acquireTestRunSession({ debug: false });

        manager.listenForLeasedDebugSessions({
            rpcServer: {} as any,
            dcpServer: {} as any,
            terminalProvider: {} as any,
            addAspireDebugSession: session => addedSessions.push(session),
            removeAspireDebugSession: session => removedSessions.push(session),
            getAspireDebugSession: () => null,
        });
        debugSessionEvents.start(createDebugSession(lease.sessionId));

        await manager.releaseTestRunSession(lease.id);
        await manager.releaseTestRunSession(lease.id);

        assert.deepStrictEqual(removedSessions, addedSessions);
        assert.strictEqual(stopDebuggingStub.calledOnce, true);
    });

    test('explicit release remains single-flight when parent termination races the stop', async () => {
        const debugSessionEvents = stubDebugSessionEvents();
        const manager = new TestRunSessionManager(connectionInfo);
        const addedSessions: AspireDebugSession[] = [];
        const lease = manager.acquireTestRunSession({ debug: false });
        const debugSession = createDebugSession(lease.sessionId);

        manager.listenForLeasedDebugSessions({
            rpcServer: {} as any,
            dcpServer: {} as any,
            terminalProvider: {} as any,
            addAspireDebugSession: session => addedSessions.push(session),
            removeAspireDebugSession: () => { },
            getAspireDebugSession: () => null,
        });
        debugSessionEvents.start(debugSession);

        const aspireDebugSession = addedSessions[0];
        assert.ok(aspireDebugSession);
        const recordTerminationSpy = sinon.spy(aspireDebugSession, 'recordParentDebugSessionTermination');
        let rejectFirstStop: ((reason: unknown) => void) | undefined;
        const firstStop = new Promise<void>((_, reject) => {
            rejectFirstStop = reject;
        });
        const stopDebuggingStub = sinon.stub(aspireDebugSession, 'stopDebugging');
        stopDebuggingStub.onFirstCall().callsFake(() => {
            debugSessionEvents.terminate(debugSession);
            return firstStop;
        });
        stopDebuggingStub.onSecondCall().resolves();

        const firstRelease = manager.releaseTestRunSession(lease.id);
        await Promise.resolve();

        assert.strictEqual(recordTerminationSpy.callCount, 1, 'The terminating parent must still resolve to its lease');
        assert.strictEqual(stopDebuggingStub.callCount, 1, 'The termination callback must join the explicit release');
        assert.strictEqual((manager as any).leases.has(lease.id), true);
        assert.strictEqual((manager as any).leasedDebugSessions.has(lease.id), true);

        const stopFailure = new Error('Failed to stop leased debug session');
        const firstReleaseFailure = assert.rejects(
            firstRelease,
            (error: unknown) => {
                assert.strictEqual(error, stopFailure);
                return true;
            });
        rejectFirstStop!(stopFailure);
        await firstReleaseFailure;

        assert.strictEqual((manager as any).leases.has(lease.id), true, 'A failed release must remain retryable');
        assert.strictEqual((manager as any).leasedDebugSessions.has(lease.id), true, 'A failed stop must remain reachable');

        await manager.releaseTestRunSession(lease.id);
        await manager.releaseTestRunSession(lease.id);

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual((manager as any).leases.has(lease.id), false);
        assert.strictEqual((manager as any).leasedDebugSessions.has(lease.id), false);
    });

    test('parent debug session termination records the parent stopped before releasing the lease', async () => {
        const debugSessionEvents = stubDebugSessionEvents();
        const manager = new TestRunSessionManager(connectionInfo);
        const addedSessions: AspireDebugSession[] = [];
        const removedSessions: AspireDebugSession[] = [];
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').rejects(new Error('Debug session already terminated'));
        const lease = manager.acquireTestRunSession({ debug: false });
        const debugSession = createDebugSession(lease.sessionId);

        manager.listenForLeasedDebugSessions({
            rpcServer: {} as any,
            dcpServer: {} as any,
            terminalProvider: {} as any,
            addAspireDebugSession: session => addedSessions.push(session),
            removeAspireDebugSession: session => removedSessions.push(session),
            getAspireDebugSession: () => null,
        });
        debugSessionEvents.start(debugSession);

        debugSessionEvents.terminate(debugSession);
        await manager.releaseTestRunSession(lease.id);

        assert.deepStrictEqual(removedSessions, addedSessions);
        assert.strictEqual(stopDebuggingStub.notCalled, true);
    });
});

function stubDebugSessionEvents(): {
    start: (session: vscode.DebugSession) => void;
    terminate: (session: vscode.DebugSession) => void;
} {
    let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
    let terminateDebugSession: ((session: vscode.DebugSession) => void) | undefined;
    sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
        startDebugSession = listener;
        return { dispose: () => { } };
    });
    sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
        terminateDebugSession = listener;
        return { dispose: () => { } };
    });

    return {
        start: session => {
            assert.ok(startDebugSession);
            startDebugSession(session);
        },
        terminate: session => {
            assert.ok(terminateDebugSession);
            terminateDebugSession(session);
        },
    };
}

function createDebugSession(sessionId: string): vscode.DebugSession {
    return {
        id: `vscode-${sessionId}`,
        type: 'coreclr',
        name: 'Aspire test run',
        workspaceFolder: undefined,
        configuration: {
            type: 'coreclr',
            name: 'Aspire test run',
            request: 'launch',
            env: {
                DCP_INSTANCE_ID_PREFIX: `${sessionId}-`,
            },
        },
        customRequest: sinon.stub(),
        getDebugProtocolBreakpoint: sinon.stub(),
    };
}
