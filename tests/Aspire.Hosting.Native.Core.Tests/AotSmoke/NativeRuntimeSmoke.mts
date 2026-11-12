// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { createConnection } from 'node:net';
import { homedir, tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from 'vscode-jsonrpc/node.js';
import './aspire.mjs';
import type { NativeApplicationServer } from './aspire.mjs';
import { AspireClient, CapabilityError, isMarshalledHandle, wrapIfHandle } from './transport.mjs';

const binary = process.argv[2];
assert.ok(binary, 'Pass the published native AppHost server executable.');
const token = randomBytes(32).toString('hex');
const workspaceDirectory = await mkdtemp(join(tmpdir(), 'native-runtime-'));
const apphostPath = join(workspaceDirectory, 'apphost.mts');
const server = spawn(binary, ['--port', '0'], {
    env: { ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token, AppHost__FilePath: apphostPath },
    stdio: ['ignore', 'pipe', 'pipe']
});
server.stderr.on('data', bytes => process.stderr.write(bytes));
const timeout = setTimeout(() => server.kill('SIGKILL'), 60_000);
const clients: AspireClient[] = [];
let auxiliary: rpc.MessageConnection | undefined;
let auxiliarySocketPath: string | undefined;
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
    await session.retireGeneration(replacement);

    const socketDirectory = join(homedir(), '.aspire', 'cli', 'bch');
    const discoveryDeadline = Date.now() + 10_000;
    while (!auxiliarySocketPath) {
        // Production auxiliary names end with their owner PID:
        // <11-char AppHost ID><8-char instance ID>.<pid>.
        const matches = (await readdir(socketDirectory)).filter(name => name.endsWith(`.${server.pid}`));
        assert.ok(matches.length <= 1);
        if (matches[0]) auxiliarySocketPath = join(socketDirectory, matches[0]);
        else {
            assert.ok(Date.now() < discoveryDeadline, 'The native server must publish its auxiliary socket.');
            await delay(50);
        }
    }
    const auxiliarySocket = createConnection(auxiliarySocketPath);
    await once(auxiliarySocket, 'connect');
    auxiliary = rpc.createMessageConnection(new rpc.StreamMessageReader(auxiliarySocket), new rpc.StreamMessageWriter(auxiliarySocket));
    auxiliary.onDispose(() => auxiliarySocket.destroy());
    auxiliary.listen();
    const info = await auxiliary.sendRequest<{ processId: number; appHostPath: string }>('GetAppHostInformationAsync');
    assert.equal(info.processId, server.pid);
    assert.equal(info.appHostPath, apphostPath);

    const workspace = await author.root.createApplicationWorkspace();
    let revision = await workspace.beginRevision();
    await revision.addResource('removed', 'custom/Resource');
    await workspace.commitRevision(revision);
    const watch = await auxiliary.sendRequest<{ token: number }>('WatchResourcesAsync', {});
    interface AuxiliarySnapshot { name: string; state: string; isHidden: boolean; version: number }
    async function nextSnapshot(): Promise<AuxiliarySnapshot> {
        assert.ok(auxiliary);
        const next = await auxiliary.sendRequest<{ values: AuxiliarySnapshot[]; finished: boolean }>('$/enumerator/next', watch.token);
        assert.equal(next.finished, false);
        assert.equal(next.values.length, 1);
        return next.values[0]!;
    }
    const initialAuxiliary = await nextSnapshot();
    assert.equal(initialAuxiliary.name, 'removed');
    await workspace.commitRevision(await workspace.beginRevision());
    assert.deepEqual((await auxiliary.sendRequest<{ resources: AuxiliarySnapshot[] }>('GetResourcesAsync', {})).resources, []);
    const removed = await nextSnapshot();
    assert.equal(removed.name, 'removed');
    assert.equal(removed.state, 'Hidden');
    assert.equal(removed.isHidden, true);
    assert.ok(removed.version > initialAuxiliary.version);
    revision = await workspace.beginRevision();
    await revision.addResource('removed', 'custom/Resource');
    await workspace.commitRevision(revision);
    const restored = await nextSnapshot();
    assert.equal(restored.name, 'removed');
    assert.equal(restored.isHidden, false);
    assert.ok(restored.version > removed.version);
    await workspace.retireApplicationWorkspace();
    assert.deepEqual((await auxiliary.sendRequest<{ resources: AuxiliarySnapshot[] }>('GetResourcesAsync', {})).resources, []);
    const retired = await nextSnapshot();
    assert.equal(retired.name, 'removed');
    assert.equal(retired.state, 'Hidden');
    assert.equal(retired.isHidden, true);
    assert.ok(retired.version > restored.version);
    await auxiliary.sendRequest('$/enumerator/abort', watch.token);
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
        generationReplacement: 'passed',
        auxiliaryRemovalAndRestoration: 'passed',
        auxiliaryWorkspaceRetirement: 'passed'
    }, null, 2));
} finally {
    auxiliary?.dispose();
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
    if (auxiliarySocketPath) {
        assert.equal((await readdir(join(homedir(), '.aspire', 'cli', 'bch'))).includes(
            basename(auxiliarySocketPath)), false);
    }
    await rm(workspaceDirectory, { recursive: true });
}
