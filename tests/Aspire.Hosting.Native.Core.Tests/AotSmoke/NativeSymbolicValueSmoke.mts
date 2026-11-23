// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { createConnection } from 'node:net';
import { createInterface } from 'node:readline';
import * as rpc from 'vscode-jsonrpc/node.js';
import './aspire.mjs';
import type { NativeApplicationServer } from './aspire.mjs';
import { AspireClient, CapabilityError, isMarshalledHandle, wrapIfHandle } from './transport.mjs';

// This is shared-contract validation, not an integration provider or an AppHost.
const binary = process.argv[2];
assert.ok(binary, 'Pass the native server executable.');
const token = randomBytes(32).toString('hex');
const server = spawn(binary, ['--port', '0'], {
    env: { ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token },
    stdio: ['ignore', 'pipe', 'pipe']
});
server.stderr.on('data', bytes => process.stderr.write(bytes));
let client: AspireClient | undefined;
try {
    const lines = createInterface({ input: server.stdout });
    const [line] = await Promise.race([
        once(lines, 'line'),
        once(server, 'exit').then(([code]) => { throw new Error(`Native server exited (${code}).`); })
    ]);
    lines.close();
    // Native launches announce: Native AppHost server listening on 127.0.0.1:<port>.
    const match = /^Native AppHost server listening on 127\.0\.0\.1:(\d+)\.$/.exec(String(line));
    assert.ok(match);
    const socket = createConnection({ host: '127.0.0.1', port: Number(match[1]) });
    await once(socket, 'connect');
    const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
    connection.listen();
    assert.equal(await connection.sendRequest('authenticate', token), true);
    client = AspireClient.fromConnection(connection, socket, message => process.stderr.write(message + '\n'));
    const handle = await connection.sendRequest('getApplicationServer');
    assert.ok(isMarshalledHandle(handle));
    const root = wrapIfHandle(handle, client) as NativeApplicationServer;
    const workspace = await root.createApplicationWorkspace();
    const initial = await workspace.beginRevision();
    const source = await initial.addResource('cache', 'example/redis');
    const target = await initial.addResource('consumer', 'example/executable');
    const password = await initial.addSecretParameter('password', 'typescript-secret');
    const endpoint = await initial.createEndpointValue(source, 'tcp', 'hostAndPort');
    const uri = await initial.createLiteralValue('redis://:')
        .concatValue(password.encodeUriValue())
        .concatValue(initial.createLiteralValue('@'))
        .concatValue(endpoint);
    await target.setSymbolicConfiguration('connection', uri);
    const metadata = (await target.inspectSymbolicConfiguration()).properties!;
    assert.equal(metadata.length, 1);
    assert.equal(metadata[0].isSecret, true);
    assert.deepEqual(metadata[0].parameterReferences, ['password']);
    assert.deepEqual(metadata[0].references, [(await source.inspectResource()).resourceId]);
    assert.deepEqual((await target.inspectResource()).dependencies, []);
    assert.deepEqual((await target.inspectResource()).configuration, []);
    await workspace.commitRevision(initial);
    const execution = await workspace.getApplicationExecution();
    const owner = await root.joinResourceExecution(await execution.inviteResourceExecution(target));
    const observer = await root.joinApplicationObserver(await execution.inviteApplicationObserver());
    await owner.appendResourceLog('stdout', 'typescript-secret');
    const targetId = (await target.inspectResource()).resourceId!;
    assert.deepEqual((await observer.readResourceLogs(targetId, 0)).entries?.map(entry => entry.message), ['[REDACTED]']);
    await assert.rejects(owner.startContainerWithValues(1, 'container', {
        image: 'redis:8', entrypoint: '', environment: [], arguments: [], mounts: [], endpoints: []
    }, {
        environment: [{ name: 'CONNECTION', configurationKey: 'connection' }], arguments: []
    }), (error: unknown) => error instanceof CapabilityError && error.code === 'VALUE_NETWORK_CONTEXT_UNSUPPORTED');
    const revision = await workspace.beginRevision();
    await revision.addResource('cache', 'example/redis');
    const updated = await revision.addResource('consumer', 'example/executable');
    await updated.setSymbolicConfiguration('connection', await revision.addSecretParameter('password', 'replacement-secret'));
    await workspace.commitRevision(revision);
    assert.equal((await owner.readResourceConfiguration()).revision, 2);
    assert.equal((await owner.inspectExecutionSymbolicConfiguration()).properties?.[0].isSecret, true);
    client.throwOnPendingRejections = false;
    await assert.rejects(async () => { await password.encodeUriValue(); }, (error: unknown) =>
        error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND');
    await client.flushPendingPromises();
    client.throwOnPendingRejections = true;
    await owner.appendResourceLog('stdout', 'typescript-secret replacement-secret');
    assert.deepEqual((await observer.readResourceLogs(targetId, 0)).entries?.map(entry => entry.message),
        ['[REDACTED]', '[REDACTED] [REDACTED]']);
    await workspace.retireApplicationWorkspace();
    await client.flushPendingPromises();
    process.stdout.write('TypeScript symbolic value contract passed.\n');
} finally {
    await client?.disconnect();
    if (server.exitCode === null && server.signalCode === null) {
        const exited = once(server, 'exit');
        server.kill('SIGTERM');
        await exited;
    }
}
