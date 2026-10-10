import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtemp, rm, stat, writeFile } from 'node:fs/promises';
import { createConnection } from 'node:net';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';
import { createNativeBuilder, type NativeResource } from './generated/aspire.mjs';
import { AspireClient, wrapIfHandle } from './generated/transport.mjs';
import { defineAnnotation, getAnnotation, registerAnnotation, setAnnotation } from './annotations.mts';

const root = resolve(new URL('.', import.meta.url).pathname, '../..');
const work = await mkdtemp(join(root, 'artifacts/native-hosting/annotations-'));
const binary = resolve(process.env.NATIVE_HOSTING_ATS_BINARY ?? join(root, 'artifacts/native-hosting/ats-server/NativeHosting.AtsServer'));
const socketPath = join(work, 'apphost.sock');
const auth = randomUUID();
const server = spawn(binary, [], {
    env: {
        ...process.env, REMOTE_APP_HOST_SOCKET_PATH: socketPath, ASPIRE_REMOTE_APPHOST_TOKEN: auth,
        NATIVE_HOSTING_DCP: process.env.NATIVE_HOSTING_DCP ??
            join(homedir(), '.nuget/packages/microsoft.developercontrolplane.darwin-arm64/0.26.5/tools/dcp'),
    },
    stdio: ['ignore', 'pipe', 'pipe'],
});
let diagnostics = '';
server.stdout.on('data', chunk => { diagnostics += chunk.toString(); });
server.stderr.on('data', chunk => { diagnostics += chunk.toString(); });
const exited = new Promise<void>((fulfill, reject) => {
    server.once('error', reject);
    server.once('close', fulfill);
});
const clients: ReturnType<typeof rpc.createMessageConnection>[] = [];
const sockets: ReturnType<typeof createConnection>[] = [];
async function waitFor(predicate: () => Promise<boolean>, description: string) {
    const deadline = Date.now() + 15000;
    while (!await predicate()) {
        if (server.exitCode !== null || server.signalCode !== null)
            throw new Error(`Server exited while waiting for ${description}: ${diagnostics}`);
        if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${description}: ${diagnostics}`);
        await delay(20);
    }
}
async function connectClient() {
    const socket = createConnection(socketPath);
    sockets.push(socket);
    await new Promise<void>((fulfill, reject) => {
        socket.once('connect', fulfill);
        socket.once('error', reject);
    });
    const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
    clients.push(connection);
    const client = AspireClient.fromConnection(connection, socket, () => {});
    connection.listen();
    assert.equal(await connection.sendRequest('authenticate', auth), true);
    return { connection, client, socket };
}

try {
    await waitFor(async () => {
        try { return (await stat(socketPath)).isSocket(); }
        catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false; throw error; }
    }, 'native socket');
    const writer = await connectClient();
    const reader = await connectClient();
    const builder = await createNativeBuilder(writer.client);
    const resource = await builder.addResource('cache', 'container', { image: 'redis:8.6' });
    const persistence = defineAnnotation<{
        intervalMs: number;
        keysChangedThreshold: number;
        enabled: boolean;
        label?: string;
    }>('example.redis/persistence', {
        intervalMs: { type: 'number', required: true },
        keysChangedThreshold: { type: 'number', required: true },
        enabled: { type: 'boolean', required: true },
        label: { type: 'string', required: false },
    });
    await registerAnnotation(builder, persistence);
    await registerAnnotation(builder, persistence);
    await builder.defineAnnotation(persistence.id, [
        { name: 'label', type: 'string', required: false },
        { name: 'enabled', type: 'boolean', required: true },
        { name: 'keysChangedThreshold', type: 'number', required: true },
        { name: 'intervalMs', type: 'number', required: true },
    ]);
    assert.equal(await resource.hasAnnotation(persistence.id), false);
    await assert.rejects(resource.getAnnotation(persistence.id), /has no annotation/);
    const initial = { intervalMs: 60000, keysChangedThreshold: 100, enabled: true };
    await setAnnotation(resource, persistence, initial);
    // A separate authenticated connection reads the same entity through real
    // generated proxies, not a client-local annotation cache.
    const foreign = wrapIfHandle(resource.toJSON(), reader.client) as NativeResource;
    assert.deepEqual(await getAnnotation(foreign, persistence), initial);
    const replacement = { intervalMs: 1000, keysChangedThreshold: 10, enabled: false, label: 'portable' };
    await setAnnotation(foreign, persistence, replacement);
    assert.deepEqual(await getAnnotation(resource, persistence), replacement);
    const emptyLabel = JSON.stringify({ ...initial, label: '' });
    const boundary = { ...initial, label: 'x'.repeat(65536 - Buffer.byteLength(emptyLabel)) };
    assert.equal(Buffer.byteLength(JSON.stringify(boundary)), 65536);
    await setAnnotation(resource, persistence, boundary);
    assert.deepEqual(await getAnnotation(foreign, persistence), boundary);
    await setAnnotation(resource, persistence, replacement);

    const invalidPayloads: [string, RegExp][] = [
        ['[]', /JSON objects/],
        ['null', /JSON objects/],
        ['{"intervalMs":"60000","keysChangedThreshold":100,"enabled":true}', /must have type 'number'/],
        ['{"intervalMs":60000,"keysChangedThreshold":100}', /Missing required.*enabled/],
        ['{"intervalMs":null,"keysChangedThreshold":100,"enabled":true}', /must have type 'number'/],
        ['{"intervalMs":60000,"keysChangedThreshold":100,"enabled":true,"unknown":1}', /Unknown.*unknown/],
        ['{"intervalMs":60000,"keysChangedThreshold":100,"enabled":{"$handle":"forged"}}', /must have type 'boolean'/],
        ['{"intervalMs":1,"intervalMs":2,"keysChangedThreshold":100,"enabled":true}', /Duplicate.*intervalMs/],
        ['{"intervalMs":1e999,"keysChangedThreshold":100,"enabled":true}', /finite number/],
        ['{invalid', /invalid|expected|start/i],
        [JSON.stringify({ ...initial, label: 'x'.repeat(65536) }), /64 KiB/],
        [JSON.stringify({ ...initial, label: '\u00e9'.repeat(32768) }), /64 KiB/],
    ];
    for (const [payload, message] of invalidPayloads) {
        await assert.rejects(async () => await resource.withAnnotation(persistence.id, payload), message);
        assert.deepEqual(await getAnnotation(foreign, persistence), replacement);
    }
    await assert.rejects(builder.defineAnnotation(persistence.id, [{ name: 'changed', type: 'string', required: true }]), /different schema/);
    await assert.rejects(builder.defineAnnotation('not-namespaced', [{ name: 'value', type: 'string', required: true }]), /namespace\/name/);
    await assert.rejects(builder.defineAnnotation('example/empty', []), /between 1 and 32/);
    await assert.rejects(builder.defineAnnotation('example/invalid', [{ name: '$handle', type: 'string', required: true }]), /field names/);
    await assert.rejects(builder.defineAnnotation('example/invalid', [{ name: 'value', type: 'object', required: true }]), /Unsupported/);
    await assert.rejects(builder.defineAnnotation('example/duplicate', [
        { name: 'value', type: 'string', required: true }, { name: 'value', type: 'number', required: true },
    ]), /Duplicate/);
    const boundedId = `n/${'a'.repeat(126)}`;
    const boundedFields = [
        ...Array.from({ length: 31 }, (_, index) => ({ name: `field${index}`, type: 'string', required: true })),
        { name: 'X'.repeat(64), type: 'string', required: true },
    ];
    assert.equal(boundedId.length, 128);
    assert.equal(boundedFields.length, 32);
    await builder.defineAnnotation(boundedId, boundedFields);
    await assert.rejects(builder.defineAnnotation(`${boundedId}a`, boundedFields), /namespace\/name/);
    await assert.rejects(builder.defineAnnotation('example/too-many', [
        ...boundedFields, { name: 'extra', type: 'string', required: true },
    ]), /between 1 and 32/);
    await assert.rejects(builder.defineAnnotation('example/too-long', [
        { name: 'X'.repeat(65), type: 'string', required: true },
    ]), /field names/);
    await assert.rejects(async () => await resource.withAnnotation('example/unknown', '{}'), /no registered schema/);
    await assert.rejects(resource.hasAnnotation('example/unknown'), /no registered schema/);

    const published = JSON.parse(await builder.publish());
    assert.deepEqual(published.annotationSchemas, {
        [persistence.id]: {
            enabled: { type: 'boolean', required: true },
            intervalMs: { type: 'number', required: true },
            keysChangedThreshold: { type: 'number', required: true },
            label: { type: 'string', required: false },
        },
        [boundedId]: Object.fromEntries(boundedFields.map(field => [field.name, { type: field.type, required: field.required }])),
    });
    assert.deepEqual(published.resources[0].annotations, { [persistence.id]: replacement });
    assert.equal((await reader.connection.sendRequest<{ stats: { dynamicCodeSupported: boolean } }>('getRuntimeState'))
        .stats.dynamicCodeSupported, false);

    writer.connection.dispose();
    writer.socket.destroy();
    await waitFor(async () => (await reader.connection.sendRequest<{ idle: boolean }>('getRuntimeState')).idle, 'graph disposal');
    await assert.rejects(foreign.getAnnotation(persistence.id), /stale or unknown/);
    const next = await connectClient();
    const nextBuilder = await createNativeBuilder(next.client);
    const nextResource = await nextBuilder.addResource('cache', 'value', {});
    await assert.rejects(nextResource.hasAnnotation(persistence.id), /no registered schema/);
    await nextBuilder.defineAnnotation(persistence.id, [{ name: 'newGeneration', type: 'boolean', required: true }]);
    await nextResource.withAnnotation(persistence.id, '{"newGeneration":true}');
    assert.deepEqual(JSON.parse(await nextResource.getAnnotation(persistence.id)), { newGeneration: true });
    const evidence = {
        generatedAtsApis: true, nativeAot: true, separateConnectionsShareModel: true,
        validatedSingletonReplacement: true, invalidPayloadsRejected: invalidPayloads.length,
        invalidWritesPreserveExistingData: true, publishedSchemasAndPayloads: true,
        exactUtf8SizeBoundary: true, schemaOrderIndependent: true,
        exactSchemaSizeBoundaries: true,
        staleHandlesRejected: true, schemasAreGraphScoped: true,
        binaryBytes: (await stat(binary)).size,
    };
    if (process.env.NATIVE_HOSTING_ANNOTATION_RESULTS)
        await writeFile(process.env.NATIVE_HOSTING_ANNOTATION_RESULTS, JSON.stringify(evidence, null, 2));
    console.log(JSON.stringify(evidence, null, 2));
} finally {
    for (const connection of clients) connection.dispose();
    for (const socket of sockets) socket.destroy();
    if (server.exitCode === null && server.signalCode === null) server.kill('SIGTERM');
    try {
        await Promise.race([exited, delay(15000, undefined, { ref: false }).then(() => { throw new Error('Native server did not exit.'); })]);
        assert.equal(server.exitCode, 0, diagnostics);
    } finally {
        if (server.exitCode === null && server.signalCode === null) {
            server.kill('SIGKILL');
            await exited;
        }
        await rm(work, { recursive: true });
    }
}
