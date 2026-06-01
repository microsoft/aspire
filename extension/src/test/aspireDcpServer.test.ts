import * as assert from 'assert';
import * as https from 'https';
import { IncomingHttpHeaders } from 'http';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import WebSocket from 'ws';
import AspireDcpServer from '../dcp/AspireDcpServer';
import waitForExpect from 'wait-for-expect';

suite('Aspire DCP server', () => {
    let dcpServer: AspireDcpServer;
    let startDebuggingStub: sinon.SinonStub;
    let stopDebuggingStub: sinon.SinonStub;
    let registerDebugAdapterTrackerFactoryStub: sinon.SinonStub;
    let startListeners: Array<(session: vscode.DebugSession) => unknown>;

    setup(() => {
        startListeners = [];
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startListeners.push(listener);
            return {
                dispose: () => {
                    startListeners = startListeners.filter(l => l !== listener);
                }
            };
        });
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, config) => {
            const debugConfig = config as vscode.DebugConfiguration;
            setImmediate(() => {
                for (const listener of startListeners) {
                    listener({
                        id: 'resource-debug-session',
                        type: debugConfig.type,
                        name: debugConfig.name,
                        workspaceFolder: undefined,
                        configuration: debugConfig,
                        customRequest: sinon.stub(),
                        getDebugProtocolBreakpoint: sinon.stub()
                    } as unknown as vscode.DebugSession);
                }
            });

            return true;
        });
        stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        registerDebugAdapterTrackerFactoryStub = sinon.stub(vscode.debug, 'registerDebugAdapterTrackerFactory').returns({ dispose: sinon.stub() });
    });

    teardown(() => {
        dcpServer?.dispose();
        sinon.restore();
    });

    test('lease-backed run session starts resource without Aspire debug session', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });
        const dcpId = `${lease.sessionId}-api`;

        const response = await requestDcpServer(dcpServer, 'PUT', '/run_session', lease.env.DEBUG_SESSION_TOKEN, dcpId, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });

        assert.strictEqual(response.statusCode, 201);
        assert.strictEqual(startDebuggingStub.calledOnce, true);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], undefined);
    });

    test('lease-backed run session rejects wrong token', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });
        const dcpId = `${lease.sessionId}-api`;

        const response = await requestDcpServer(dcpServer, 'PUT', '/run_session', 'wrong-token', dcpId, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ]
        });

        assert.strictEqual(response.statusCode, 401);
        assert.strictEqual(startDebuggingStub.called, false);
    });

    test('release stops lease-backed run sessions and revokes token', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });
        const dcpId = `${lease.sessionId}-api`;

        const response = await requestDcpServer(dcpServer, 'PUT', '/run_session', lease.env.DEBUG_SESSION_TOKEN, dcpId, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });

        assert.strictEqual(response.statusCode, 201);

        await dcpServer.releaseTestRunSession(lease.id);

        const rejectedResponse = await requestDcpServer(dcpServer, 'PUT', '/run_session', lease.env.DEBUG_SESSION_TOKEN, dcpId, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });

        assert.strictEqual(stopDebuggingStub.calledOnce, true);
        assert.strictEqual(rejectedResponse.statusCode, 401);
    });

    test('lease-backed run session registers debug adapter tracker', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });

        const response = await requestDcpServer(dcpServer, 'PUT', '/run_session', lease.env.DEBUG_SESSION_TOKEN, `${lease.sessionId}-api`, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });

        assert.strictEqual(response.statusCode, 201);
        assert.strictEqual(registerDebugAdapterTrackerFactoryStub.calledOnceWith('pwa-node'), true);
    });

    test('lease token cannot delete another lease run session', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const firstLease = dcpServer.acquireTestRunSession({ debug: true });
        const secondLease = dcpServer.acquireTestRunSession({ debug: true });
        const firstDcpId = `${firstLease.sessionId}-api`;
        const secondDcpId = `${secondLease.sessionId}-api`;

        const response = await requestDcpServer(dcpServer, 'PUT', '/run_session', firstLease.env.DEBUG_SESSION_TOKEN, firstDcpId, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });
        const runId = getRunIdFromLocation(response.headers.location);

        const deleteResponse = await requestDcpServer(dcpServer, 'DELETE', `/run_session/${runId}`, secondLease.env.DEBUG_SESSION_TOKEN, secondDcpId, {});

        assert.strictEqual(deleteResponse.statusCode, 404);
        assert.strictEqual(stopDebuggingStub.called, false);
    });

    test('release during lease-backed startup stops started resource and returns gone', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });
        let debugConfig: vscode.DebugConfiguration | undefined;

        startDebuggingStub.callsFake(async (_folder, config) => {
            debugConfig = config as vscode.DebugConfiguration;
            return true;
        });

        const responsePromise = requestDcpServer(dcpServer, 'PUT', '/run_session', lease.env.DEBUG_SESSION_TOKEN, `${lease.sessionId}-api`, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });

        await waitForExpect(() => assert.ok(debugConfig));
        await dcpServer.releaseTestRunSession(lease.id);
        emitStartedDebugSession(startListeners, debugConfig!);

        const response = await responsePromise;

        assert.strictEqual(response.statusCode, 410);
        assert.strictEqual(stopDebuggingStub.calledOnce, true);
    });

    test('dispose stops lease-backed run sessions', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });

        const response = await requestDcpServer(dcpServer, 'PUT', '/run_session', lease.env.DEBUG_SESSION_TOKEN, `${lease.sessionId}-api`, {
            launch_configurations: [
                {
                    type: 'node',
                    mode: 'Debug',
                    script_path: '/workspace/app.js'
                }
            ],
            args: [],
            env: []
        });

        assert.strictEqual(response.statusCode, 201);

        dcpServer.dispose();

        assert.strictEqual(stopDebuggingStub.calledOnce, true);
    });

    test('lease-backed notification websocket rejects wrong token', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });

        await assert.rejects(
            connectNotifyWebSocket(dcpServer, 'wrong-token', `${lease.sessionId}-api`));
    });

    test('release closes lease-backed notification websocket', async () => {
        dcpServer = await AspireDcpServer.create(() => null);
        const lease = dcpServer.acquireTestRunSession({ debug: true });
        const ws = await connectNotifyWebSocket(dcpServer, lease.env.DEBUG_SESSION_TOKEN, `${lease.sessionId}-api`);

        const closed = new Promise<void>(resolve => ws.once('close', () => resolve()));

        await dcpServer.releaseTestRunSession(lease.id);

        await closed;
    });
});

async function requestDcpServer(
    server: AspireDcpServer,
    method: string,
    path: string,
    token: string,
    dcpId: string,
    body: unknown): Promise<{ statusCode?: number; body: string; headers: IncomingHttpHeaders }> {
    const [, port] = server.connectionInfo.address.split(':');
    const bodyJson = JSON.stringify(body);

    return new Promise((resolve, reject) => {
        const req = https.request({
            hostname: 'localhost',
            port,
            path,
            method,
            rejectUnauthorized: false,
            headers: {
                Authorization: `Bearer ${token}`,
                'microsoft-developer-dcp-instance-id': dcpId,
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(bodyJson)
            }
        }, res => {
            let responseBody = '';
            res.on('data', chunk => responseBody += chunk);
            res.on('end', () => resolve({ statusCode: res.statusCode, body: responseBody, headers: res.headers }));
        });

        req.on('error', reject);
        req.write(bodyJson);
        req.end();
    });
}

function emitStartedDebugSession(
    listeners: Array<(session: vscode.DebugSession) => unknown>,
    debugConfig: vscode.DebugConfiguration): void {
    for (const listener of listeners) {
        listener({
            id: 'resource-debug-session',
            type: debugConfig.type,
            name: debugConfig.name,
            workspaceFolder: undefined,
            configuration: debugConfig,
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        } as unknown as vscode.DebugSession);
    }
}

function getRunIdFromLocation(location: string | string[] | undefined): string {
    assert.ok(typeof location === 'string');

    const match = /\/run_session\/([^/]+)$/.exec(location);
    assert.ok(match);

    return match[1];
}

async function connectNotifyWebSocket(
    server: AspireDcpServer,
    token: string,
    dcpId: string): Promise<WebSocket> {
    const [, port] = server.connectionInfo.address.split(':');

    return new Promise((resolve, reject) => {
        const ws = new WebSocket(`wss://localhost:${port}/run_session/notify`, {
            rejectUnauthorized: false,
            headers: {
                Authorization: `Bearer ${token}`,
                'microsoft-developer-dcp-instance-id': dcpId
            }
        });

        ws.once('open', () => resolve(ws));
        ws.once('error', reject);
        ws.once('close', () => reject(new Error('WebSocket closed before opening.')));
    });
}
