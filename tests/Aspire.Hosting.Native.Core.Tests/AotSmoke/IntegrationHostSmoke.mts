// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createServer, type Socket } from 'node:net';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as rpc from 'vscode-jsonrpc/node.js';
import { AspireExport, CancellationToken, defineIntegration, unregisterCancellation } from './base.mjs';
import {
    getIntegrationInvocationSignal, IntegrationResource, runIntegrationHost, type JsonObject, type RemoteHandle,
} from './integration-host.mjs';

const resourceType = { typeId: 'example.host/Resource', category: 'Handle' } as const;
const textType = { typeId: 'string', category: 'Primitive' } as const;

async function provider(): Promise<void> {
    let disposed = 0;
    let inUse = false;
    class Resource extends IntegrationResource {
        constructor(readonly behavior: string) {
            super({ toJSON: () => ({ $handle: 'declaration', $type: 'test/DeclaredResource' }) }, resourceType.typeId);
        }
        override async dispose(): Promise<void> {
            disposed++;
            process.send!('disposing');
            assert.equal(inUse, false, 'retirement must drain nested resource invocations before disposal');
            if (this.behavior === 'hang') await new Promise<void>(() => {});
            if (this.behavior === 'error') throw new Error('private provider failure');
        }
    }
    const create = AspireExport({
        id: 'example.host/create', method: 'create', description: 'Creates a provider-owned test resource.',
        projection: { parameters: [{ name: 'behavior', type: textType }], returnType: resourceType },
    }, async ({ behavior }: { behavior: string }) => new Resource(behavior));
    const collision = AspireExport({
        id: 'example.host/collision', method: 'collision', description: 'Preserves authored arguments during cancellation.',
        projection: { parameters: [{ name: 'signal', type: textType },
            { name: 'cancellationToken', type: { typeId: 'cancellationToken', category: 'Primitive' } }],
        returnType: textType },
    }, async (args: { signal: string; cancellationToken: CancellationToken }) => {
        assert.equal(args.signal, 'authored-signal');
        assert.deepEqual(Object.keys(args).sort(), ['cancellationToken', 'signal']);
        assert(args.cancellationToken instanceof CancellationToken);
        const signal = getIntegrationInvocationSignal(args);
        const tokenId = args.cancellationToken.register();
        assert.equal(typeof tokenId, 'string');
        process.send!('started');
        await new Promise<void>(resolve => signal.addEventListener('abort', () => resolve(), { once: true }));
        // Registering an already-aborted projected token must reject, proving
        // it shares host cancellation rather than retaining the wire value.
        assert.throws(() => args.cancellationToken.register(), /aborted/);
        unregisterCancellation(tokenId!);
        return args.signal;
    });
    const nested = AspireExport({
        id: 'example.host/nested', method: 'nested', description: 'Uses nested provider resources until drained.',
        projection: { parameters: [], returnType: textType },
    }, async (args: JsonObject) => {
        const collect = (value: unknown): Resource[] => value instanceof Resource ? [value]
            : Array.isArray(value) ? value.flatMap(collect)
            : value && typeof value === 'object' ? Object.values(value).flatMap(collect) : [];
        assert(collect(args).length > 0, 'nested handles must be decoded into actual provider resources');
        inUse = true;
        const signal = getIntegrationInvocationSignal(args);
        process.send!('started');
        await new Promise<void>(resolve => signal.addEventListener('abort', () => resolve(), { once: true }));
        // A parent-controlled gate, not a sleep, keeps user work active after
        // cancellation so the test can observe whether release waits for it.
        const drained = once(process, 'message');
        process.send!('aborted');
        assert.equal((await drained)[0], 'drain');
        inUse = false;
        return 'drained';
    });
    const status = AspireExport({
        id: 'example.host/status', method: 'status', description: 'Reports observed disposal and invocation state.',
    }, async () => ({ disposed, inUse }));
    await runIntegrationHost({ packageName: 'example.host',
        integrations: [defineIntegration({ name: 'HostRegression', capabilities: [create, collision, nested, status] })] });
}

async function scenario(mode: 'managed' | 'native', action: 'behavior' | 'disconnect' | 'SIGTERM' | 'error'): Promise<void> {
    let connected!: (connection: rpc.MessageConnection) => void;
    let ready!: () => void;
    const connectionReady = new Promise<rpc.MessageConnection>(resolve => { connected = resolve; });
    const registration = new Promise<void>(resolve => { ready = resolve; });
    let socket: Socket | undefined;
    const server = createServer(peer => {
        socket = peer;
        const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(peer), new rpc.StreamMessageWriter(peer));
        connection.onRequest('authenticate', () => true);
        connection.onRequest('ping', () => 'pong');
        connection.onRequest('cancelToken', () => true);
        connection.onRequest('registerAsIntegrationHost', async () => {
            const metadata = await connection.sendRequest<{ protocolVersion: number }>('getCapabilities');
            assert.equal(metadata.protocolVersion, 2);
            ready();
            return true;
        });
        connection.listen();
        connected(connection);
    });
    if (mode === 'native') {
        server.listen(0, '127.0.0.1');
    } else {
        server.listen(process.platform === 'win32' ? `\\\\.\\pipe\\aspire-host-${process.pid}`
            : resolve(`artifacts/ih-${process.pid}.sock`));
    }
    await once(server, 'listening');
    const address = server.address()!;
    const child = spawn(process.execPath, [fileURLToPath(import.meta.url), '--provider'], {
        env: { ...process.env,
            REMOTE_APP_HOST_SOCKET_PATH: mode === 'managed' ? address as string : '',
            ASPIRE_NATIVE_RPC_ENDPOINT: mode === 'native' && typeof address !== 'string' ? `127.0.0.1:${address.port}` : '',
            ASPIRE_INTEGRATION_HOST_REGISTRATION_ID: 'test-registration',
            ASPIRE_REMOTE_APPHOST_TOKEN: 'test-token', ASPIRE_NATIVE_RPC_TOKEN: 'test-token',
        },
        stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
    });
    let stderr = '';
    let stdout = '';
    child.stderr!.setEncoding('utf8').on('data', data => { stderr += data; });
    child.stdout!.setEncoding('utf8').on('data', data => { stdout += data; });
    const closed = once(child, 'close');
    // Every wait, including a broken startup, has an external process deadline.
    const deadline = setTimeout(() => child.kill('SIGKILL'), 20000);
    const events: unknown[] = [];
    let eventReady: (() => void) | undefined;
    child.on('message', message => { events.push(message); eventReady?.(); });
    const event = async (expected: string) => {
        while (events.length === 0) {
            await Promise.race([new Promise<void>(resolve => { eventReady = resolve; }),
                closed.then(() => { throw new Error(`Provider exited before ${expected}: ${stderr}\n${stdout}`); })]);
        }
        assert.equal(events.shift(), expected);
    };
    let connection: rpc.MessageConnection | undefined;
    try {
        connection = await Promise.race([connectionReady, closed.then(() => { throw new Error(stderr); })]);
        await Promise.race([registration, closed.then(() => { throw new Error(stderr); })]);
        let invocation = 0;
        const invoke = <T,>(name: string, args: JsonObject = {}, token?: rpc.CancellationToken) =>
            connection!.sendRequest<T>('handleExternalCapability', `example.host/${name}`, args, String(++invocation),
                ...(token ? [token] : []));
        if (action === 'behavior') {
            const cancellation = new rpc.CancellationTokenSource();
            const collision = invoke<string>('collision', { signal: 'authored-signal', cancellationToken: null }, cancellation.token);
            await event('started');
            cancellation.cancel();
            assert.equal(await collision, 'authored-signal');
            cancellation.dispose();

            for (const shape of ['array', 'object', 'mixed']) {
                const first = await invoke<RemoteHandle>('create', { behavior: 'normal' });
                const second = await invoke<RemoteHandle>('create', { behavior: 'normal' });
                const nested = shape === 'array' ? [first, second, first]
                    : shape === 'object' ? { first, second } : { items: [{ first }, [second, first]] };
                const before = await invoke<{ disposed: number }>('status');
                const active = invoke<string>('nested', { nested });
                await event('started');
                const releases = [first, second].map(handle => connection!.sendRequest<boolean>('releaseIntegrationResource', handle.$handle));
                await event('aborted');
                assert.deepEqual(await invoke('status'), { disposed: before.disposed, inUse: true });
                child.send('drain');
                assert.equal(await active, 'drained');
                assert.deepEqual(await Promise.all(releases), [true, true]);
                await event('disposing');
                await event('disposing');
                assert.deepEqual(await invoke('status'), { disposed: before.disposed + 2, inUse: false });
            }
        } else {
            await invoke('create', { behavior: action === 'error' ? 'error' : 'hang' });
        }
        if (action === 'SIGTERM') child.kill('SIGTERM');
        else socket!.destroy();
        if (action !== 'behavior') await event('disposing');
        const [code, signal] = await closed;
        assert.equal(signal, null, `provider required external kill: ${stderr}`);
        assert.equal(code, action === 'behavior' ? 0 : 1, stderr);
        assert.equal(stderr, action === 'behavior' ? '' : action === 'error'
            ? 'Integration object cleanup failed; forcing process exit.\n'
            : 'Integration object cleanup timed out; forcing process exit.\n');
    } finally {
        clearTimeout(deadline);
        if (child.exitCode === null && child.signalCode === null) {
            child.kill('SIGKILL');
            await closed;
        }
        connection?.dispose();
        socket?.destroy();
        await new Promise<void>(resolve => server.close(() => resolve()));
    }
}

if (process.argv[2] === '--provider') {
    await provider();
} else {
    for (const mode of ['managed', 'native'] as const) {
        await scenario(mode, 'behavior');
    }
    for (const action of ['disconnect', 'SIGTERM', 'error'] as const) {
        await scenario('native', action);
    }
    console.log('integration-host-ok');
}
