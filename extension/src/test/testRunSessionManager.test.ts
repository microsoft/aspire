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

    test('parent debug session termination releases the matching lease', async () => {
        const debugSessionEvents = stubDebugSessionEvents();
        const manager = new TestRunSessionManager(connectionInfo);
        const addedSessions: AspireDebugSession[] = [];
        const removedSessions: AspireDebugSession[] = [];
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
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
        assert.strictEqual(stopDebuggingStub.calledOnce, true);
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
