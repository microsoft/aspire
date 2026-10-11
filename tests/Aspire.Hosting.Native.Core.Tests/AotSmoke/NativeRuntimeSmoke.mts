// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { createConnection } from 'node:net';
import { createInterface } from 'node:readline';
import * as rpc from 'vscode-jsonrpc/node.js';
import './aspire.mjs';
import type { NativeApplicationServer } from './aspire.mjs';
import { AspireClient, CapabilityError, isMarshalledHandle, wrapIfHandle } from './transport.mjs';

const binary = process.argv[2];
assert.ok(binary, 'Pass the published native AppHost server executable.');
const token = randomBytes(32).toString('hex');
const server = spawn(binary, ['--port', '0'], {
    env: { ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token },
    stdio: ['ignore', 'pipe', 'pipe']
});
server.stderr.on('data', bytes => process.stderr.write(bytes));
const timeout = setTimeout(() => server.kill('SIGKILL'), 60_000);
const clients: AspireClient[] = [];
try {
    // The listener binds before publishing this line. This fixture observes the
    // endpoint, then authenticates through the real generated SDK transport.
    const lines = createInterface({ input: server.stdout });
    const [line]: [string] = await Promise.race([
        once(lines, 'line') as Promise<[string]>,
        once(server, 'exit').then(([code]) => { throw new Error(`Native server exited during startup (${code}).`); })
    ]);
    lines.close();
    const match = /^Native AppHost server listening on 127\.0\.0\.1:(\d+)\.$/.exec(line);
    assert.ok(match, 'The native server must advertise its bound loopback endpoint.');
    const port = Number(match[1]);

    async function connect(): Promise<{ client: AspireClient; root: NativeApplicationServer }> {
        const socket = createConnection({ host: '127.0.0.1', port });
        await once(socket, 'connect');
        const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
        connection.listen();
        assert.equal(await connection.sendRequest('authenticate', token), true);
        const contract = await connection.sendRequest('getCapabilities');
        assert.deepEqual(contract, JSON.parse(readFileSync(new URL('../contract.json', import.meta.url), 'utf8')));
        const client = AspireClient.fromConnection(connection, socket, message => process.stderr.write(message + '\n'));
        clients.push(client);
        const handle = await connection.sendRequest('getApplicationServer');
        assert.ok(isMarshalledHandle(handle));
        const root = wrapIfHandle(handle, client);
        assert.ok(root && typeof root === 'object' && 'openApplication' in root);
        return { client, root: root as NativeApplicationServer };
    }

    const author = await connect();
    const integration = await connect();
    const dashboard = await connect();
    const session = await author.root.openApplication();
    const composition = await session.startGeneration();
    const cache = await composition.addResource('cache', 'example/Redis');
    const execution = await composition.createExecution();
    const writer = await integration.root.joinResourceExecution(await execution.inviteResourceExecution(cache));
    const observer = await dashboard.root.joinApplicationObserver(await execution.inviteApplicationObserver());
    const initial = await observer.readResourceObservations();
    assert.ok(initial.version !== undefined);
    const changed = observer.waitResourceObservations(initial.version, 30000);
    await writer.publishObservation({ state: 'Running', healthy: true, urls: ['http://localhost:8000'] });
    const snapshot = await changed;
    assert.ok(snapshot.resources);
    assert.equal(snapshot.resources[0].state, 'Running');
    assert.equal(snapshot.resources[0].healthy, true);
    assert.deepEqual(snapshot.resources[0].urls, ['http://localhost:8000']);
    assert.ok(snapshot.resources[0].resourceId);
    await writer.appendResourceLog('stdout', 'integration ready');
    const logs = await observer.readResourceLogs(snapshot.resources[0].resourceId, 0);
    assert.deepEqual(logs.entries?.map(entry => entry.message), ['integration ready']);
    await writer.defineResourceCommand('refresh', 'Refresh');
    const command = await observer.invokeResourceCommand('cache', 'refresh');
    const completed = command.awaitRuntimeOperation();
    const pending = await writer.readResourceCommands();
    assert.ok(pending.commands?.[0].requestId);
    await writer.completeResourceCommand(pending.commands[0].requestId, { status: 'succeeded', message: 'refreshed' });
    assert.equal((await completed).status, 'succeeded');
    const prompt = await writer.requestConfirmation('Continue?');
    const answer = prompt.awaitRuntimeOperation();
    const requests = await observer.readConfirmations();
    assert.ok(requests.requests?.[0].requestId);
    await observer.respondConfirmation(requests.requests[0].requestId, true);
    assert.equal((await answer).status, 'accepted');
    await author.client.flushPendingPromises();
    await integration.client.flushPendingPromises();
    await dashboard.client.flushPendingPromises();
    await session.retireGeneration(composition);
    integration.client.throwOnPendingRejections = false;
    await assert.rejects(async () => { await writer.appendResourceLog('stdout', 'late'); }, (error: unknown) =>
        error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND');
    await integration.client.flushPendingPromises();
    integration.client.throwOnPendingRejections = true;
    // A new composition replaces declarations without restarting the native
    // server or its independently authenticated integration connection.
    const replacement = await session.startGeneration();
    await replacement.addResource('cache', 'example/Redis');
    assert.equal((await replacement.inspect()).resources?.length, 1);
    author.client.disconnect();
    integration.client.disconnect();
    dashboard.client.disconnect();
    console.log(JSON.stringify({
        nativeSocketRuntimeRoundTrips: 'passed',
        independentConnections: 3,
        observations: 'passed',
        logs: 'passed',
        command: 'passed',
        confirmation: 'passed',
        generationReplacement: 'passed'
    }, null, 2));
} finally {
    for (const client of clients) {
        client.disconnect();
    }
    const exited = server.exitCode === null && server.signalCode === null
        ? once(server, 'exit')
        : Promise.resolve([server.exitCode]);
    if (server.exitCode === null && server.signalCode === null) {
        server.kill('SIGINT');
    }
    const [code] = await exited;
    clearTimeout(timeout);
    assert.equal(code, 0);
}
