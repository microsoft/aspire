import * as assert from 'assert';
import * as sinon from 'sinon';
import * as tls from 'tls';
import { createMessageConnection, ErrorCodes, MessageConnection, ResponseError } from 'vscode-jsonrpc';
import { StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';

import AspireRpcServer from '../../server/AspireRpcServer';
import { ICliRpcClient, RpcClient } from '../../server/rpcClient';
import { extensionLogOutputChannel } from '../../utils/logging';

suite('AspireRpcServer', () => {
    test('server disposal owns a client while its debug-session handshake is pending', async function () {
        this.timeout(10000);

        const handshakeStarted = createDeferred<void>();
        const handshakeResult = createDeferred<string | null>();
        let rpcClient: RpcClient | undefined;
        const rpcServer = await AspireRpcServer.create((_connectionInfo, connection) => {
            rpcClient = new RpcClient(connection, null, () => null);
            return rpcClient;
        });
        const transport = await connectClient(rpcServer, async () => {
            handshakeStarted.resolve();
            return await handshakeResult.promise;
        });
        const transportClosed = new Promise<void>(resolve => transport.socket.once('close', () => resolve()));

        try {
            await handshakeStarted.promise;
            assert.ok(rpcClient);

            rpcClient.interactionService.showStatus('Building AppHost...');
            assert.strictEqual((rpcClient.interactionService as any)._progressNotifier.isActive, true);

            rpcServer.dispose();
            await transportClosed;

            assert.strictEqual((rpcClient.interactionService as any)._progressNotifier.isActive, false);
            assert.strictEqual(transport.socket.destroyed, true);
        }
        finally {
            handshakeResult.resolve(null);
            await new Promise<void>(resolve => setImmediate(resolve));
            transport.connection.end();
            transport.connection.dispose();
            transport.socket.destroy();
            rpcClient?.dispose();
        }
    });

    test('a rejected debug-session handshake on an open transport is warned and disposes the pending client', async function () {
        this.timeout(10000);

        const handshakeStarted = createDeferred<void>();
        const handshakeResult = createDeferred<string | null>();
        const clientDisposed = createDeferred<void>();
        const warnStub = sinon.stub(extensionLogOutputChannel, 'warn');
        let rpcClient: RpcClient | undefined;
        const rpcServer = await AspireRpcServer.create((_connectionInfo, connection) => {
            rpcClient = new RpcClient(connection, null, () => null);
            const originalDispose = rpcClient.dispose.bind(rpcClient);
            rpcClient.dispose = () => {
                originalDispose();
                clientDisposed.resolve();
            };
            return rpcClient;
        });
        const transport = await connectClient(rpcServer, async () => {
            handshakeStarted.resolve();
            return await handshakeResult.promise;
        });

        try {
            await handshakeStarted.promise;
            assert.ok(rpcClient);

            rpcClient.interactionService.showStatus('Connecting to AppHost...');
            handshakeResult.reject(new ResponseError(ErrorCodes.PendingResponseRejected, 'handshake failed'));
            await clientDisposed.promise;

            sinon.assert.calledWithMatch(warnStub, 'Failed to initialize RPC client:');
            assert.strictEqual((rpcClient.interactionService as any)._progressNotifier.isActive, false);
            assert.deepStrictEqual(rpcServer.connections, []);
        }
        finally {
            transport.connection.end();
            transport.connection.dispose();
            transport.socket.destroy();
            warnStub.restore();
            rpcServer.dispose();
        }
    });

    test('transport disposal during the handshake logs the expected pending rejection at info', async function () {
        this.timeout(10000);

        const handshakeStarted = createDeferred<void>();
        const handshakeResult = createDeferred<string | null>();
        const clientDisposed = createDeferred<void>();
        const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
        const warnStub = sinon.stub(extensionLogOutputChannel, 'warn');
        let rpcClient: RpcClient | undefined;
        const rpcServer = await AspireRpcServer.create((_connectionInfo, connection) => {
            rpcClient = new RpcClient(connection, null, () => null);
            const originalDispose = rpcClient.dispose.bind(rpcClient);
            rpcClient.dispose = () => {
                originalDispose();
                clientDisposed.resolve();
            };
            return rpcClient;
        });
        const transport = await connectClient(rpcServer, async () => {
            handshakeStarted.resolve();
            return await handshakeResult.promise;
        });

        try {
            await handshakeStarted.promise;
            assert.ok(rpcClient);

            rpcClient.interactionService.showStatus('Connecting to AppHost...');
            transport.connection.end();
            transport.connection.dispose();
            transport.socket.destroy();
            await clientDisposed.promise;
            await new Promise<void>(resolve => setImmediate(resolve));

            sinon.assert.calledWithMatch(infoStub, 'RPC client transport closed during initialization:');
            assert.strictEqual(warnStub.calledWithMatch('Failed to initialize RPC client:'), false);
            assert.strictEqual((rpcClient.interactionService as any)._progressNotifier.isActive, false);
            assert.deepStrictEqual(rpcServer.connections, []);
        }
        finally {
            handshakeResult.resolve(null);
            infoStub.restore();
            warnStub.restore();
            rpcServer.dispose();
        }
    });

    test('connections added after disposal are rejected and disposed without publishing them', async () => {
        const rpcServer = await AspireRpcServer.create(() => {
            throw new Error('The test does not establish a connection.');
        });
        const onNewConnection = sinon.spy();
        rpcServer.onNewConnection(onNewConnection);
        const closeSpy = sinon.spy(rpcServer.server, 'close');
        let clientDisposeCount = 0;
        const client = {
            dispose: () => clientDisposeCount++,
        } as unknown as ICliRpcClient;

        rpcServer.dispose();
        rpcServer.dispose();
        const added = rpcServer.addConnection(client);

        assert.strictEqual(added, false);
        assert.strictEqual(clientDisposeCount, 1);
        assert.deepStrictEqual(rpcServer.connections, []);
        sinon.assert.notCalled(onNewConnection);
        sinon.assert.calledOnce(closeSpy);
    });
});

async function connectClient(
    rpcServer: AspireRpcServer,
    getDebugSessionId: () => Promise<string | null>
): Promise<{ connection: MessageConnection; socket: tls.TLSSocket }> {
    const port = Number(rpcServer.connectionInfo.address.replace('localhost:', ''));
    const socket = tls.connect({
        port,
        host: 'localhost',
        rejectUnauthorized: false,
    });
    await new Promise<void>((resolve, reject) => {
        socket.once('secureConnect', resolve);
        socket.once('error', reject);
    });

    const connection = createMessageConnection(
        new StreamMessageReader(socket),
        new StreamMessageWriter(socket)
    );
    connection.onRequest('getDebugSessionId', getDebugSessionId);
    connection.listen();

    return { connection, socket };
}

function createDeferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void } {
    let resolve!: (value: T) => void;
    let reject!: (reason: unknown) => void;
    const promise = new Promise<T>((promiseResolve, promiseReject) => {
        resolve = promiseResolve;
        reject = promiseReject;
    });

    return { promise, resolve, reject };
}
