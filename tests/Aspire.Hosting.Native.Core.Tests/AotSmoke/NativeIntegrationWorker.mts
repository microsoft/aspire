// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { connectNativeAppHost } from './native-client.mjs';
import type { AspireClient } from './transport.mjs';
import type { ResourceExecution, StartedWorkload } from './aspire.mjs';
import { CapabilityError } from './transport.mjs';
import { redisCommand } from './NativeRedisClient.mjs';

interface Configuration {
    port: number;
    socketPath?: string;
    token: string;
    password: string;
    redisInvitation: string;
    postgresInvitation: string;
    tunnelInvitation: string;
    configurationUpdates?: boolean;
}
let stopped = false;
let client: AspireClient | undefined;
let loop: Promise<void> | undefined;
const expectedRetirements = new Set<string>();
assert.ok(process.connected && process.send && process.disconnect, 'The integration fixture requires a parent-owned IPC channel.');
const disconnect = process.disconnect.bind(process);
const send = process.send.bind(process);
process.on('message', message => {
    if (message && typeof message === 'object' && 'type' in message && message.type === 'expectRetirement' &&
        'resources' in message && Array.isArray(message.resources)) {
        for (const name of message.resources) {
            assert.ok(typeof name === 'string');
            expectedRetirements.add(name);
        }
        send({ type: 'retirementExpected' });
    }
    if (message === 'stop') {
        stopped = true;
        client?.disconnect();
        void Promise.resolve(loop).then(() => { if (process.connected) disconnect(); });
    }
});

async function postgresQuery(endpoint: StartedWorkload, password: string, sql: string): Promise<pg.QueryResult> {
    const database = new pg.Client({
        host: endpoint.host, port: endpoint.port, user: 'postgres', password, database: 'postgres',
        connectionTimeoutMillis: 2000, query_timeout: 5000
    });
    try {
        await database.connect();
        return await database.query(sql);
    } finally {
        await database.end();
    }
}

async function waitForProbe(probe: () => Promise<unknown>): Promise<void> {
    const deadline = Date.now() + 30000;
    while (true) {
        try {
            await probe();
            return;
        } catch (error) {
            // Connection refusal/reset and PostgreSQL's 57P03 mean the
            // allocated workload is still starting, not a successful probe.
            // pg also emits the code-less "Connection terminated unexpectedly"
            // while postgres' initialization server shuts down before final startup.
            const code = error && typeof error === 'object' && 'code' in error ? error.code : undefined;
            const initializationDisconnect = error instanceof Error && error.message === 'Connection terminated unexpectedly';
            if ((!['ECONNREFUSED', 'ECONNRESET', '57P03'].includes(String(code)) && !initializationDisconnect) ||
                Date.now() >= deadline) throw error;
            await delay(100);
        }
    }
}

async function run(configuration: Configuration): Promise<void> {
    assert.ok((configuration.port > 0 || configuration.socketPath) && configuration.token && configuration.password);
    const connection = await connectNativeAppHost({
        endpoint: configuration.socketPath ?? { host: '127.0.0.1', port: configuration.port },
        authenticationToken: configuration.token
    });
    client = connection.client;
    const redis = await connection.server.joinResourceExecution(configuration.redisInvitation);
    const postgres = await connection.server.joinResourceExecution(configuration.postgresInvitation);
    const tunnel = await connection.server.joinResourceExecution(configuration.tunnelInvitation);
    const cacheReady = (async () => {
        const endpoint = await redis.startContainer({ image: 'redis:8.6', targetPort: 6379, environment: [], arguments: [] });
        assert.ok(endpoint.host && endpoint.port);
        await waitForProbe(() => redisCommand(endpoint.host!, endpoint.port!, ['PING']));
        assert.equal(await redisCommand(endpoint.host, endpoint.port, ['SET', 'native-proof', 'native-value']), 'OK');
        assert.equal(await redisCommand(endpoint.host, endpoint.port, ['GET', 'native-proof']), 'native-value');
        await redis.appendResourceLog('stdout', 'Redis integration initialized through its allocated endpoint.');
        await redis.publishObservation({ state: 'Running', healthy: true, urls: [`tcp://${endpoint.host}:${endpoint.port}`] });
        await redis.defineResourceCommand('read-value', 'Read value');
        return endpoint;
    })();
    const databaseReady = (async () => {
        const endpoint = await postgres.startContainer({
            image: 'postgres:18.3', targetPort: 5432,
            environment: [{ name: 'POSTGRES_PASSWORD', value: configuration.password }], arguments: []
        });
        await waitForProbe(() => postgresQuery(endpoint, configuration.password, 'SELECT 1'));
        await postgresQuery(endpoint, configuration.password,
            "CREATE TABLE native_proof(value text NOT NULL); INSERT INTO native_proof VALUES ('native-value')");
        const query = await postgresQuery(endpoint, configuration.password, 'SELECT value FROM native_proof');
        assert.deepEqual(query.rows, [{ value: 'native-value' }]);
        await postgres.appendResourceLog('stdout', 'PostgreSQL integration initialized through its allocated endpoint.');
        await postgres.publishObservation({ state: 'Running', healthy: true, urls: [`postgresql://${endpoint.host}:${endpoint.port}`] });
        return endpoint;
    })();
    const tunnelReady = (async () => {
        const cache = await cacheReady;
        const endpoint = await tunnel.startExecutable({
            executablePath: process.execPath, workingDirectory: fileURLToPath(new URL('.', import.meta.url)),
            portEnvironmentVariable: 'FORWARD_PORT', environment: [],
            arguments: [fileURLToPath(new URL('./NativeTunnelFixture.mjs', import.meta.url)), String(cache.port)]
        });
        assert.ok(endpoint.host && endpoint.port);
        await waitForProbe(() => redisCommand(endpoint.host!, endpoint.port!, ['PING']));
        assert.equal(await redisCommand(endpoint.host, endpoint.port, ['GET', 'native-proof']), 'native-value');
        await tunnel.publishObservation({ state: 'Running', healthy: true, urls: [`tcp://${endpoint.host}:${endpoint.port}`] });
        return endpoint;
    })();
    let [cache, database, relay] = await Promise.all([cacheReady, databaseReady, tunnelReady]);
    const confirmation = await postgres.requestConfirmation('Keep the initialized database running?');
    assert.equal((await confirmation.awaitRuntimeOperation()).status, 'accepted');
    await client.flushPendingPromises();
    const work = [commands(redis, () => cache)];
    if (configuration.configurationUpdates) {
        // Establish cursors before announcing readiness. A replacement AppHost
        // can commit immediately after that announcement.
        const [cacheConfiguration, tunnelConfiguration] = await Promise.all([
            redis.readResourceConfiguration(), tunnel.readResourceConfiguration()
        ]);
        assert.ok(cacheConfiguration.revision !== undefined && tunnelConfiguration.revision !== undefined);
        work.push(watchConfiguration(redis, 'cache', cacheConfiguration.revision, async revision => {
            const desired = await redis.readResourceConfiguration();
            const values = new Map((desired.properties ?? []).map(property => [property.name, property.value]));
            const memory = values.get('maxmemory');
            if (memory) {
                cache = await redis.restartContainer(revision, {
                    image: 'redis:8.6', targetPort: 6379, environment: [],
                    arguments: ['--maxmemory', memory]
                });
                await waitForProbe(() => redisCommand(cache.host!, cache.port!, ['PING']));
                await redis.defineResourceCommand('read-value', 'Read value');
            }
            assert.ok(cache.host && cache.port);
            const value = values.get('proofValue') ?? 'native-value';
            assert.equal(await redisCommand(cache.host, cache.port, ['SET', 'native-proof', value]), 'OK');
            await redis.completeResourceConfiguration(revision, { status: 'succeeded', message: '' });
            await redis.publishObservation({ state: 'Running', healthy: true, urls: [`tcp://${cache.host}:${cache.port}`] });
            send({ type: 'configurationApplied', resource: 'cache', revision, endpoint: cache });
        }));
        work.push(watchConfiguration(tunnel, 'tunnel', tunnelConfiguration.revision, async revision => {
            let dependencies = await tunnel.readResourceDependencies();
            const end = Date.now() + 45_000;
            while ((dependencies.resources ?? []).some(resource => !resource.healthy)) {
                assert.ok(!stopped && Date.now() < end, 'Tunnel dependency did not become healthy.');
                await delay(100);
                dependencies = await tunnel.readResourceDependencies();
            }
            const cacheResource = dependencies.resources?.find(resource => resource.name === 'cache');
            assert.ok(cacheResource?.urls?.[0]);
            const endpoint = new URL(cacheResource.urls[0]);
            relay = await tunnel.restartExecutable(revision, {
                executablePath: process.execPath, workingDirectory: fileURLToPath(new URL('.', import.meta.url)),
                portEnvironmentVariable: 'FORWARD_PORT', environment: [],
                arguments: [fileURLToPath(new URL('./NativeTunnelFixture.mjs', import.meta.url)), endpoint.port]
            });
            assert.ok(relay.host && relay.port);
            await waitForProbe(() => redisCommand(relay.host!, relay.port!, ['PING']));
            await tunnel.completeResourceConfiguration(revision, { status: 'succeeded', message: '' });
            await tunnel.publishObservation({ state: 'Running', healthy: true, urls: [`tcp://${relay.host}:${relay.port}`] });
            send({ type: 'configurationApplied', resource: 'tunnel', revision, endpoint: relay });
        }));
    }
    loop = Promise.all(work).then(() => undefined).catch(reportFailure);
    send({ type: 'ready', cache, database, relay, postgresQueryValue: 'native-value' });
}

async function commands(resource: ResourceExecution, getEndpoint: () => StartedWorkload): Promise<void> {
    while (!stopped) {
        try {
            const pending = await resource.waitResourceCommands(1000);
            for (const command of pending.commands ?? []) {
                assert.ok(command.requestId && command.name === 'read-value');
                const endpoint = getEndpoint();
                const value = await redisCommand(endpoint.host!, endpoint.port!, ['GET', 'native-proof']);
                await resource.completeResourceCommand(command.requestId, { status: 'succeeded', message: value });
            }
        } catch (error) {
            if (stopped || expectedRetirements.has('cache') && error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND') return;
            throw error;
        }
    }
}

async function watchConfiguration(resource: ResourceExecution, name: string, revision: number,
    apply: (revision: number) => Promise<void>): Promise<void> {
    while (!stopped) {
        try {
            const desired = await resource.waitResourceConfiguration(revision, 1000);
            assert.ok(desired.revision !== undefined);
            if (desired.revision !== revision) {
                await apply(desired.revision);
                revision = desired.revision;
            }
        } catch (error) {
            if (stopped || expectedRetirements.has(name) && error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND') return;
            throw error;
        }
    }
}

function reportFailure(error: unknown): void {
    const classification = error instanceof CapabilityError
        ? { name: error.name, code: error.code, capability: error.error.capability, message: error.message }
        : { name: error instanceof Error ? error.name : 'unknown' };
    console.error('Native integration failure:', classification);
    client?.disconnect();
    if (process.connected) send({ type: 'failed', classification });
    process.exitCode = 1;
    if (process.connected) disconnect();
}

process.once('message', configuration => { void run(configuration as Configuration).catch(reportFailure); });
