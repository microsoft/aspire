// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import childProcess, { type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { syncBuiltinESMExports } from 'node:module';
import { createServer, type Socket } from 'node:net';
import * as rpc from 'vscode-jsonrpc/node.js';
import { nativeTypes } from './native-types.mjs';

// Capture actual owned subprocesses, without replacing spawn/kill/stdio with
// mock behavior. The generated client imports this synchronized builtin export.
const originalSpawn = childProcess.spawn;
const children: ChildProcess[] = [];
const signals = new Map<ChildProcess, (NodeJS.Signals | number | undefined)[]>();
childProcess.spawn = ((...args: Parameters<typeof originalSpawn>) => {
    const child = originalSpawn(...args);
    const kill = child.kill.bind(child);
    const sent: (NodeJS.Signals | number | undefined)[] = [];
    signals.set(child, sent);
    child.kill = signal => { sent.push(signal); return kill(signal); };
    children.push(child);
    return child;
}) as typeof originalSpawn;
syncBuiltinESMExports();
const { connectNativeAppHost } = await import('./native-client.mjs');

const originalSetTimeout = globalThis.setTimeout;
const originalClearTimeout = globalThis.clearTimeout;
const deadline = originalSetTimeout(() => {
    for (const child of children) {
        if (child.pid && child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    }
    throw new Error('Generated provider shutdown smoke exceeded its bounded test deadline.');
}, 30000);
const timers = new Set<ReturnType<typeof setTimeout>>();
globalThis.setTimeout = ((callback: (...args: unknown[]) => void, milliseconds?: number, ...args: unknown[]) => {
    const timer = originalSetTimeout(() => {
        timers.delete(timer);
        callback(...args);
    }, milliseconds);
    if (milliseconds === 100 || milliseconds === 5000 || milliseconds === 15000) timers.add(timer);
    return timer;
}) as typeof setTimeout;
globalThis.clearTimeout = ((timer: ReturnType<typeof setTimeout>) => {
    timers.delete(timer);
    originalClearTimeout(timer);
}) as typeof clearTimeout;

let ready!: () => void;
let registered = Promise.resolve();
let rejectRegistration = false;
let stallAbandonment = false;
let nextAuthority = 0;
const pending = new Map<string, { abandoned: Promise<void>; abandon: () => void }>();
const sockets: Socket[] = [];
const connections: rpc.MessageConnection[] = [];
const server = createServer(socket => {
    sockets.push(socket);
    const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
    connections.push(connection);
    connection.onRequest('authenticate', () => true);
    connection.onRequest('getApplicationServer', () => ({ $handle: 'test-root', $type: nativeTypes.NativeApplicationServer.typeId }));
    connection.onRequest('expectIntegrationHost', () => {
        assert(pending.size < 64, 'activation capacity must be released after failed spawn');
        const authority = `registration-${nextAuthority++}`;
        let abandon!: () => void;
        const abandoned = new Promise<void>(resolve => { abandon = resolve; });
        pending.set(authority, { abandoned, abandon });
        return authority;
    });
    connection.onRequest('abandonIntegrationHostRegistration', (authority: string) => {
        const activation = pending.get(authority);
        if (!activation) return false;
        pending.delete(authority);
        activation.abandon();
        if (stallAbandonment) return new Promise<boolean>(() => {});
        return true;
    });
    connection.onRequest('waitIntegrationHostRegistration', async (authority: string) => {
        const activation = pending.get(authority);
        assert(activation);
        await Promise.race([registered, activation.abandoned]);
        if (!pending.has(authority)) throw new Error('activation abandoned');
        if (rejectRegistration) throw new Error('registration rejected');
        pending.delete(authority);
        return true;
    });
    connection.listen();
});
const readiness = createServer(socket => {
    sockets.push(socket);
    // The test-only child sends "ready" after installing its signal handler.
    // This socket checks subprocess startup, not application/service readiness.
    socket.once('data', data => { assert.equal(data.toString(), 'ready'); ready(); socket.end(); });
});
server.listen(0, '127.0.0.1');
readiness.listen(0, '127.0.0.1');
await Promise.all([once(server, 'listening'), once(readiness, 'listening')]);
const address = server.address();
const readinessAddress = readiness.address();
assert(address && typeof address !== 'string' && readinessAddress && typeof readinessAddress !== 'string');
const native = await connectNativeAppHost({
    endpoint: { host: '127.0.0.1', port: address.port },
    authenticationToken: 'test-token',
    authenticationTimeoutMilliseconds: 15000,
    providerCleanupTimeoutMilliseconds: 100,
});

const childSource = `
    const net = require('node:net');
    process.on('SIGTERM', () => { if (process.argv[2] === 'graceful') process.exit(0); });
    const socket = net.connect(Number(process.argv[1]), '127.0.0.1');
    socket.on('connect', () => socket.end('ready'));
    setInterval(() => {}, 1000);
`;
function prepare(): void {
    registered = new Promise<void>(resolve => { ready = resolve; });
}
try {
    for (const mode of ['graceful', 'stubborn']) {
        prepare();
        const activation = await native.activateProvider('test-provider', process.execPath,
            ['-e', childSource, String(readinessAddress.port), mode]);
        const first = activation.close(), second = activation.close();
        assert.equal(first, second, 'concurrent callers must join the same cleanup');
        await first;
        const child = children.at(-1)!;
        assert.equal(child.exitCode, mode === 'graceful' ? 0 : null);
        assert.equal(child.signalCode, mode === 'graceful' ? null : 'SIGKILL');
        assert.deepEqual(signals.get(child), mode === 'graceful' ? ['SIGTERM'] : ['SIGTERM', 'SIGKILL']);
        assert(child.stdout?.destroyed && child.stderr?.destroyed, 'close must await stream release');
        assert.equal(timers.size, 0, 'grace/force/auth timers must be cleared');
        await activation.close();
    }

    prepare();
    rejectRegistration = true;
    await assert.rejects(native.activateProvider('test-provider', process.execPath,
        ['-e', childSource, String(readinessAddress.port), 'stubborn']), /registration rejected/);
    assert.equal(children.at(-1)!.signalCode, 'SIGKILL', 'failed activation must still stop its owned child');
    assert.equal(timers.size, 0);

    registered = Promise.resolve();
    const failure = await native.activateProvider('test-provider', `${process.execPath}.does-not-exist`, [])
        .then(() => { throw new Error('Missing executable cannot activate.'); }, error => error);
    assert(failure instanceof AggregateError);
    assert.equal(failure.errors.length, 2, 'activation and spawn/release failures must both remain visible');
    assert.match(String(failure.errors[0]), /registration rejected|ENOENT/);
    assert.match(String(failure.errors[1]), /ENOENT/);
    assert.equal(timers.size, 0);

    rejectRegistration = false;
    registered = new Promise<void>(() => {});
    for (let index = 0; index < 80; index++) {
        const spawnError = await native.activateProvider('test-provider', `${process.execPath}.does-not-exist`, [])
            .then(() => { throw new Error('Missing executable cannot activate.'); }, error => error);
        assert(spawnError instanceof AggregateError);
        assert.equal(spawnError.errors.length, 2);
        assert.match(String(spawnError.errors[0]), /ENOENT/);
        assert.match(String(spawnError.errors[1]), /ENOENT/);
        assert.equal(pending.size, 0);
        assert.equal(timers.size, 0);
    }
    await assert.rejects(native.activateProvider('test-provider', '\0', []), TypeError);
    assert.equal(pending.size, 0, 'synchronous spawn errors must also abandon authority');

    prepare();
    rejectRegistration = true;
    stallAbandonment = true;
    const stalled = await native.activateProvider('test-provider', process.execPath,
        ['-e', childSource, String(readinessAddress.port), 'graceful'])
        .then(() => { throw new Error('Rejected activation cannot succeed.'); }, error => error);
    assert(stalled instanceof AggregateError);
    assert.equal(stalled.errors.length, 2);
    assert.match(String(stalled.errors[0]), /registration rejected/);
    assert.match(String(stalled.errors[1]), /cleanup timed out/);
    assert.equal(children.at(-1)!.exitCode, 0, 'stalled authority cleanup must still release the owned child');
    assert(children.at(-1)!.stdout?.destroyed && children.at(-1)!.stderr?.destroyed);
    assert.equal(timers.size, 0);
} finally {
    for (const child of children) {
        if (child.pid && child.exitCode === null && child.signalCode === null) {
            const closed = once(child, 'close');
            child.kill('SIGKILL');
            await closed;
        }
    }
    await native.client.disconnect();
    for (const connection of connections) connection.dispose();
    for (const socket of sockets) socket.destroy();
    await Promise.all([new Promise<void>(resolve => server.close(() => resolve())),
        new Promise<void>(resolve => readiness.close(() => resolve()))]);
    childProcess.spawn = originalSpawn;
    syncBuiltinESMExports();
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;
    originalClearTimeout(deadline);
}
console.log('provider-shutdown-ok');
