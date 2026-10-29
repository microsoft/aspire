import * as assert from 'assert';
import { once } from 'events';
import * as http from 'http';
import * as net from 'net';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import WebSocket, { WebSocketServer } from 'ws';
import AspireDcpServer from '../dcp/AspireDcpServer';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { createBlazorWasmDebugRelay, registerBlazorWasmDebugRelay } from '../debugger/blazorWasmDebugRelay';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { AspireResourceExtendedDebugConfiguration } from '../dcp/types';
import AspireRpcServer from '../server/AspireRpcServer';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';

suite('Blazor WASM debug relay', () => {
    const cleanups: Array<() => void | Promise<void>> = [];
    let provider: vscode.DebugConfigurationProvider;

    setup(() => {
        sinon.stub(vscode.debug, 'registerDebugConfigurationProvider').callsFake((_type, registeredProvider) => {
            provider = registeredProvider;
            return { dispose() { } };
        });
    });

    teardown(async () => {
        cleanupRun('relay-run');
        for (const cleanup of cleanups.splice(0).reverse()) {
            await cleanup();
        }
        sinon.restore();
    });

    async function bridge(holdUpgrade = false) {
        const requests: http.IncomingMessage[] = [];
        const received: Array<{ data: string; binary: boolean }> = [];
        const sockets = new Set<net.Socket>();
        const upgrades = new WebSocketServer({ noServer: true });
        const server = http.createServer();
        server.on('connection', socket => {
            sockets.add(socket);
            socket.on('close', () => sockets.delete(socket));
        });
        const accept = (request: http.IncomingMessage, socket: net.Socket, head: Buffer) => {
            upgrades.handleUpgrade(request, socket, head, client => {
                client.on('message', (data, binary) => {
                    received.push({ data: data.toString(), binary });
                    client.send(data, { binary });
                });
            });
        };
        let release: () => void = () => { throw new Error('No pending bridge opening.'); };
        let resolveOpening!: () => void;
        const opening = new Promise<void>(resolve => { resolveOpening = resolve; });
        server.on('upgrade', (request, socket, head) => {
            requests.push(request);
            const upgrade = () => accept(request, socket as net.Socket, head);
            release = upgrade;
            if (!holdUpgrade) {
                upgrade();
            }
            else {
                // An HTTP upgrade leaves the raw socket half-open until its owner handles EOF.
                socket.on('end', () => socket.destroy());
                socket.resume();
            }
            resolveOpening();
        });
        await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
        const address = server.address();
        assert.ok(address && typeof address !== 'string');
        cleanups.push(async () => {
            for (const socket of upgrades.clients) {
                socket.terminate();
            }
            for (const socket of sockets) {
                socket.destroy();
            }
            upgrades.close();
            await new Promise<void>(resolve => server.close(() => resolve()));
        });
        return {
            url: `ws://127.0.0.1:${address.port}`,
            requests, received, opening,
            connections: upgrades.clients,
            release: () => release(),
        };
    }

    async function connect(inspectUri: string, target = '/devtools/browser/test') {
        const socket = new WebSocket(inspectUri.replace('{browserInspectUriPath}', target));
        cleanups.push(() => { socket.terminate(); });
        await once(socket, 'open');
        return socket;
    }

    async function relay(upstream: string) {
        const result = await createBlazorWasmDebugRelay(upstream, 30000);
        cleanups.push(() => result.dispose());
        return result;
    }

    function configuration(overrides: Partial<AspireResourceExtendedDebugConfiguration> = {}): AspireResourceExtendedDebugConfiguration {
        return {
            name: 'Managed browser',
            type: 'pwa-msedge',
            request: 'launch',
            runId: 'relay-run',
            debugSessionId: 'relay-dcp',
            resourceType: 'browser',
            ...overrides,
        };
    }

    async function resolve(configuration: vscode.DebugConfiguration, token?: vscode.CancellationToken) {
        const cancellation = new vscode.CancellationTokenSource();
        cleanups.push(() => cancellation.dispose());
        assert.ok(provider.resolveDebugConfigurationWithSubstitutedVariables);
        return await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, configuration, token ?? cancellation.token);
    }

    async function browserSession(upstream: string, starts: boolean) {
        const started = new vscode.EventEmitter<vscode.DebugSession>();
        const terminated = new vscode.EventEmitter<vscode.DebugSession>();
        cleanups.push(() => started.dispose(), () => terminated.dispose());
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(started.event);
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(terminated.event);
        sinon.stub(vscode.debug, 'registerDebugAdapterTrackerFactory').returns({ dispose() { } });
        const parent: vscode.DebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: { type: 'aspire', request: 'launch', name: 'Aspire' },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const owner = new AspireDebugSession(
            parent,
            sinon.createStubInstance(AspireRpcServer),
            sinon.createStubInstance(AspireDcpServer),
            sinon.createStubInstance(AspireTerminalProvider),
            () => { });
        cleanups.push(() => owner.finalizeForExtensionShutdown());
        let resolved: vscode.DebugConfiguration | undefined;
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, debugConfiguration) => {
            assert.ok(typeof debugConfiguration !== 'string');
            const candidate = await resolve({
                ...debugConfiguration,
                type: 'pwa-msedge',
                inspectUri: `${upstream}{browserInspectUriPath}`,
            });
            assert.ok(candidate);
            resolved = candidate;
            if (starts) {
                started.fire({ ...parent, id: 'browser-session', type: resolved.type, configuration: resolved });
            }
            return starts;
        });
        const resource = await owner.startAndGetDebugSession(configuration({ type: 'blazorwasm' }));
        assert.ok(resolved);
        return { owner, resource, resolved, terminate: () => {
            assert.ok(resource);
            terminated.fire(resource.session);
        } };
    }

    test('an abandoned opening never reaches the bridge or displaces a live retry', async () => {
        const upstream = await bridge();
        const guard = await relay(upstream.url);
        const abandoned = await connect(guard.inspectUri);
        const abandonedClosed = once(abandoned, 'close');
        abandoned.close();
        await abandonedClosed;

        const live = await connect(guard.inspectUri);
        const response = once(live, 'message');
        live.send('live-command');
        const [data] = await response;

        assert.strictEqual(data.toString(), 'live-command');
        assert.strictEqual(upstream.requests.length, 1);
        assert.strictEqual(upstream.requests[0].url, '/devtools/browser/test');
        assert.deepStrictEqual(upstream.received, [{ data: 'live-command', binary: false }]);
    });

    test('acknowledges locally and buffers commands while a bridge upgrade is pending', async () => {
        const upstream = await bridge(true);
        const guard = await relay(upstream.url);
        const client = await connect(guard.inspectUri, '/devtools/page/page-id?value=one%2Ftwo');
        const responses: Array<{ data: string; binary: boolean }> = [];
        let complete!: () => void;
        const received = new Promise<void>(resolve => { complete = resolve; });
        client.on('message', (data, binary) => {
            responses.push({ data: data.toString(), binary });
            if (responses.length === 2) {
                complete();
            }
        });
        client.send('first');
        client.send(Buffer.from('second'));
        await upstream.opening;
        assert.strictEqual(client.readyState, WebSocket.OPEN);
        assert.strictEqual(upstream.requests.length, 1);
        upstream.release();
        await received;

        assert.strictEqual(upstream.requests[0].url, '/devtools/page/page-id?value=one%2Ftwo');
        assert.deepStrictEqual(upstream.received, [
            { data: 'first', binary: false },
            { data: 'second', binary: true },
        ]);
        assert.deepStrictEqual(responses, upstream.received);
    });

    test('disposal closes clients and an upstream whose opening is still pending', async () => {
        const upstream = await bridge(true);
        const guard = await relay(upstream.url);
        const client = await connect(guard.inspectUri);
        client.send('start');
        await upstream.opening;
        const clientClosed = once(client, 'close');
        const requestClosed = once(upstream.requests[0].socket, 'close');
        guard.dispose();
        guard.dispose();
        await Promise.all([clientClosed, requestClosed]);

        assert.strictEqual(client.readyState, WebSocket.CLOSED);
        assert.strictEqual(upstream.requests[0].socket.destroyed, true);
    });

    test('an upstream handshake failure closes the IDE', async () => {
        const upstream = await bridge(true);
        const guard = await relay(upstream.url);
        const client = await connect(guard.inspectUri);
        const closed = once(client, 'close');
        client.send('start');
        await upstream.opening;
        upstream.requests[0].socket.end('HTTP/1.1 503 Unavailable\r\nContent-Length: 0\r\n\r\n');
        await closed;

        assert.strictEqual(client.readyState, WebSocket.CLOSED);
    });

    for (const timeout of [undefined, 3000]) {
        test(`closes a stalled bridge upgrade at the ${timeout ?? 'default'} launch deadline`, async () => {
            const upstream = await bridge(true);
            const clock = sinon.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'], shouldClearNativeTimers: true });
            cleanups.push(() => clock.restore());
            registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
            const resolved = await resolve(configuration({ inspectUri: `${upstream.url}{browserInspectUriPath}`, timeout }));
            assert.ok(resolved);
            const client = await connect(resolved.inspectUri);
            // Browser startup consumes part of the same launch budget before its first CDP command.
            await clock.tickAsync(1000);
            client.send('start');
            await upstream.opening;
            const clientClosed = once(client, 'close');
            const upstreamClosed = once(upstream.requests[0].socket, 'close');
            // An incomplete upgrade can remain active on the wire without ever becoming usable.
            upstream.requests[0].socket.write('HTTP/1.1 101 Switching Protocols\r\n');
            await clock.tickAsync((timeout ?? 10000) - 1001);
            assert.strictEqual(client.readyState, WebSocket.OPEN);
            await clock.tickAsync(1);
            await Promise.all([clientClosed, upstreamClosed]);

            assert.strictEqual(client.readyState, WebSocket.CLOSED);
            assert.strictEqual(upstream.requests[0].socket.destroyed, true);
        });
    }

    for (const timeout of [0, -1]) {
        test(`preserves the disabled launch timeout ${timeout}`, async () => {
            const upstream = await bridge(true);
            const clock = sinon.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'], shouldClearNativeTimers: true });
            cleanups.push(() => clock.restore());
            registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
            const resolved = await resolve(configuration({ inspectUri: `${upstream.url}{browserInspectUriPath}`, timeout }));
            assert.ok(resolved);
            const client = await connect(resolved.inspectUri);
            const response = once(client, 'message');
            client.send('start');
            await upstream.opening;
            await clock.tickAsync(60000);
            assert.strictEqual(client.readyState, WebSocket.OPEN);
            upstream.release();

            const [data] = await response;
            assert.strictEqual(data.toString(), 'start');
        });
    }

    test('the launch deadline does not close an established debugger transport', async () => {
        const upstream = await bridge();
        const clock = sinon.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'], shouldClearNativeTimers: true });
        cleanups.push(() => clock.restore());
        registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
        const resolved = await resolve(configuration({ inspectUri: `${upstream.url}{browserInspectUriPath}`, timeout: 3000 }));
        assert.ok(resolved);
        const client = await connect(resolved.inspectUri);
        const firstResponse = once(client, 'message');
        client.send('start');
        await firstResponse;
        await clock.tickAsync(60000);
        const response = once(client, 'message');
        client.send('still-debugging');

        const [data] = await response;
        assert.strictEqual(data.toString(), 'still-debugging');
    });

    for (const stopOutcome of ['delayed', 'rejected']) {
        test(`keeps the connected relay alive during a ${stopOutcome} browser stop`, async () => {
            const upstream = await bridge();
            let acknowledgeStop!: () => void;
            let rejectStop!: (error: Error) => void;
            const stopRequest = new Promise<void>((resolve, reject) => {
                acknowledgeStop = resolve;
                rejectStop = reject;
            });
            const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').returns(stopRequest);
            const { resource, resolved, terminate } = await browserSession(upstream.url, true);
            assert.ok(resource);
            const client = await connect(resolved.inspectUri);
            const firstResponse = once(client, 'message');
            client.send('start');
            await firstResponse;
            const stopping = Promise.resolve(resource.stopSession());
            if (stopOutcome === 'rejected') {
                const rejected = assert.rejects(stopping, /Stop rejected/);
                rejectStop(new Error('Stop rejected'));
                await rejected;
            }
            else {
                const pendingResponse = Promise.race([
                    once(client, 'message').then(([data]) => data.toString()),
                    once(client, 'close').then(() => 'connection-closed'),
                ]);
                client.send('stop-pending');
                assert.strictEqual(await pendingResponse, 'stop-pending');
                acknowledgeStop();
                await Promise.resolve();
            }
            const response = Promise.race([
                once(client, 'message').then(([data]) => data.toString()),
                once(client, 'close').then(() => 'connection-closed'),
            ]);
            client.send('still-debugging');
            assert.strictEqual(await response, 'still-debugging');

            stopDebugging.resolves();
            const retry = stopOutcome === 'rejected' ? resource.stopSession() : stopping;
            const closed = once(client, 'close');
            terminate();
            await Promise.all([retry, closed]);
        });
    }

    test('final parent disposal releases the relay even when the browser stop rejects', async () => {
        const upstream = await bridge();
        sinon.stub(vscode.debug, 'stopDebugging').rejects(new Error('Final stop rejected'));
        const { owner, resolved } = await browserSession(upstream.url, true);
        const client = await connect(resolved.inspectUri);
        const closed = once(client, 'close');
        owner.finalizeForExtensionShutdown();
        await closed;
    });

    test('a failed browser launch releases its resolved relay', async () => {
        const upstream = await bridge();
        const { resource, resolved } = await browserSession(upstream.url, false);
        assert.strictEqual(resource, undefined);
        const client = new WebSocket(resolved.inspectUri.replace('{browserInspectUriPath}', '/devtools/browser/test'));
        cleanups.push(() => { client.terminate(); });

        await assert.rejects(once(client, 'open'), /ECONNREFUSED/);
        assert.deepStrictEqual(upstream.requests, []);
    });

    for (const closedBy of ['IDE', 'bridge']) {
        test(`closing an established ${closedBy} connection releases its peer`, async () => {
            const upstream = await bridge();
            const guard = await relay(upstream.url);
            const client = await connect(guard.inspectUri);
            const response = once(client, 'message');
            client.send('start');
            await response;
            const [peer] = upstream.connections;
            const closed = Promise.all([once(client, 'close'), once(peer, 'close')]);
            (closedBy === 'IDE' ? client : peer).close();
            await closed;

            assert.strictEqual(client.readyState, WebSocket.CLOSED);
            assert.strictEqual(peer.readyState, WebSocket.CLOSED);
        });
    }

    for (const invalid of ['wrong-token', 'wrong-path', 'browser-origin']) {
        test(`rejects ${invalid} without contacting the bridge and accepts a valid retry`, async () => {
            const upstream = await bridge();
            const guard = await relay(upstream.url);
            const url = invalid === 'wrong-token'
                ? guard.inspectUri.replace(/\/[^/]+\{browserInspectUriPath\}$/, '/incorrect/devtools/browser/test')
                : guard.inspectUri.replace('{browserInspectUriPath}', invalid === 'wrong-path' ? '/json/version' : '/devtools/browser/test');
            const client = new WebSocket(url, invalid === 'browser-origin' ? { origin: 'https://example.com' } : {});
            cleanups.push(() => { client.terminate(); });
            await assert.rejects(once(client, 'open'));
            assert.deepStrictEqual(upstream.requests, []);
            const live = await connect(guard.inspectUri);
            const message = once(live, 'message');
            live.send('live-command');

            const [data] = await message;
            assert.strictEqual(data.toString(), 'live-command');
        });
    }

    test('rewrites only the native inspectUri and releases it with the owned run', async () => {
        const upstream = await bridge();
        const original = configuration({ inspectUri: `${upstream.url}{browserInspectUriPath}`, timeout: 30000, env: { PRESERVED: 'yes' } });
        const registration = registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
        cleanups.push(() => registration.dispose());
        const resolved = await resolve({ ...original });
        assert.ok(resolved);
        assert.notStrictEqual(resolved.inspectUri, original.inspectUri);
        assert.deepStrictEqual({ ...resolved, inspectUri: original.inspectUri }, original);
        assert.deepStrictEqual(await resolve({ ...resolved }), resolved);
        const client = await connect(resolved.inspectUri);
        const closed = once(client, 'close');
        cleanupRun('relay-run');
        await closed;
        assert.deepStrictEqual(upstream.requests, []);
    });

    for (const type of ['chrome', 'msedge', 'pwa-chrome', 'pwa-msedge']) {
        test(`supports the C# generated ${type} browser configuration`, async () => {
            const registration = registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
            cleanups.push(() => registration.dispose());
            const resolved = await resolve(configuration({ type, inspectUri: 'ws://localhost:1234{browserInspectUriPath}' }));
            assert.ok(resolved);
            assert.strictEqual(resolved.type, type);
            const client = await connect(resolved.inspectUri);
            assert.strictEqual(client.readyState, WebSocket.OPEN);
        });
    }

    for (const overrides of [
        { runId: 'other-run' },
        { debugSessionId: 'other-dcp' },
        { resourceType: 'project' },
        { type: 'monovsdbg_wasm' },
        { inspectUri: '{wsProtocol}://{url.hostname}:{url.port}/_framework/debug/ws-proxy?browser={browserInspectUri}' },
        { inspectUri: 'ws://example.com:1234{browserInspectUriPath}' },
        { inspectUri: 'ws://user:password@localhost:1234{browserInspectUriPath}' },
        { inspectUri: 'ws://localhost:1234/unexpected{browserInspectUriPath}' },
        { inspectUri: 'invalid{browserInspectUriPath}' },
    ]) {
        test(`preserves unrelated or non-native configuration ${JSON.stringify(overrides)}`, async () => {
            const registration = registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
            cleanups.push(() => registration.dispose());
            const original = configuration({ inspectUri: 'ws://localhost:1234{browserInspectUriPath}', ...overrides });
            const resolved = await resolve({ ...original });
            assert.deepStrictEqual(resolved, original);
        });
    }

    test('cancels initialization without returning a usable relay', async () => {
        const registration = registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
        cleanups.push(() => registration.dispose());
        const cancellation = new vscode.CancellationTokenSource();
        cleanups.push(() => cancellation.dispose());
        const pending = resolve(configuration({ inspectUri: 'ws://localhost:1234{browserInspectUriPath}' }), cancellation.token);
        cancellation.cancel();

        assert.strictEqual(await pending, undefined);
        const retry = await resolve(configuration({ inspectUri: 'ws://localhost:1234{browserInspectUriPath}' }));
        assert.ok(retry);
        const client = await connect(retry.inspectUri);
        assert.strictEqual(client.readyState, WebSocket.OPEN);
    });

    test('canceling one pending resolution does not dispose an overlapping retry', async () => {
        const registration = registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
        cleanups.push(() => registration.dispose());
        const cancellation = new vscode.CancellationTokenSource();
        cleanups.push(() => cancellation.dispose());
        const pending = resolve(configuration({ inspectUri: 'ws://localhost:1234{browserInspectUriPath}' }), cancellation.token);
        cancellation.cancel();
        const retry = resolve(configuration({ inspectUri: 'ws://localhost:1234{browserInspectUriPath}' }));

        assert.strictEqual(await pending, undefined);
        const resolved = await retry;
        assert.ok(resolved);
        const client = await connect(resolved.inspectUri);
        assert.strictEqual(client.readyState, WebSocket.OPEN);
    });

    test('run cleanup during initialization cancels the configuration', async () => {
        registerBlazorWasmDebugRelay(configuration({ type: 'blazorwasm' }));
        const pending = resolve(configuration({ inspectUri: 'ws://localhost:1234{browserInspectUriPath}' }));
        cleanupRun('relay-run');

        assert.strictEqual(await pending, undefined);
    });
});
