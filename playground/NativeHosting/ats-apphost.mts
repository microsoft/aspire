import assert from 'node:assert/strict';
import { join } from 'node:path';
import { readFile, writeFile, rm } from 'node:fs/promises';
import type { NativeBuilder } from './generated/aspire.mjs';
import { CancellationToken } from './generated/transport.mjs';

const none = () => new CancellationToken();

async function consumerResult(directory: string, endpoint: string) {
    const deadline = Date.now() + 15000;
    while (true) {
        try {
            const result = JSON.parse(await readFile(join(directory, 'consumer.json'), 'utf8'));
            assert.deepEqual(result, { endpoint, status: 200, body: { status: 'healthy' } });
            return;
        } catch (error) {
            if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
            if (Date.now() >= deadline) throw new Error('Tunnel URL consumer did not produce its health result.');
            await new Promise(resolve => setTimeout(resolve, 100));
        }
    }
}

export async function buildAppHost(builder: NativeBuilder, directory: string, generation: string) {
    const cache = await builder.addRedis('cache');
    const server = await builder.addPostgres('postgres');
    const database = await server.addDatabase('app-db', 'odd"db');
    const web = await builder.addNuxt('web', join(directory, 'web'), cache);
    await web.withEnvironment('MANAGED_LITERAL', generation);
    const tunnel = await builder.addDevTunnel('tunnel', web, directory, true);
    const consumer = await builder.addResource('tunnel-consumer', 'executable', {
        executable: process.execPath, directory, runOnly: true,
    });
    await consumer.withArgument(new URL('./tunnel-consumer.mts', import.meta.url).pathname)
        .withEnvironment('TUNNEL_URL', await tunnel.getProperty('url'))
        .withEnvironment('TUNNEL_RESULT', join(directory, 'consumer.json')).waitFor(tunnel);
    return { builder, cache, server, database, web, tunnel, consumer };
}

export async function exerciseAppHost(graph: Awaited<ReturnType<typeof buildAppHost>>, directory: string, generation: string) {
    const { builder, cache, server, database, web, tunnel, consumer } = graph;
    const before = await builder.publish();
    assert.deepEqual(JSON.parse(before).resources.map((resource: { name: string }) => resource.name),
        ['app-db', 'cache', 'cache-password', 'postgres', 'postgres-password', 'web']);
    await builder.run(none());
    assert.equal(await cache.redisCommand('PING', '', '', false, none()), 'PONG');
    await assert.rejects(cache.redisCommand('PING', '', '', true, none()), /authentication/);
    await assert.rejects(database.query('SELECT 1', true, none()), /protocol operation failed/);
    assert.equal(await database.query('CREATE TABLE ats_probe(value text); INSERT INTO ats_probe VALUES (\'passed\'); SELECT value FROM ats_probe;', false, none()),
        'CREATE TABLE\nINSERT 0 1\npassed');
    assert.equal(await cache.redisCommand('SET', 'ats-persistence', 'passed', false, none()), 'OK');
    const webUrl = await web.resolve('url', 'run', 'host', none());
    const redisResponse = await fetch(new URL('/api/redis?key=ats-persistence', webUrl));
    assert.equal(redisResponse.status, 200);
    assert.deepEqual(await redisResponse.json(), { value: 'Nuxt read/write through an owner-resolved Redis reference', commands: 3 });
    assert.deepEqual(await (await fetch(new URL('/api/bridge', webUrl))).json(), { literal: generation });
    const tunnelUrl = await tunnel.resolve('url', 'run', 'host', none());
    const health = await fetch(new URL('/api/health', tunnelUrl));
    assert.equal((await health.json()).status, 'healthy');
    await consumerResult(directory, tunnelUrl);
    assert.equal((await tunnel.status(none())).pid, null);
    await consumer.command('stop', none());
    await tunnel.command('stop', none());
    await assert.rejects(tunnel.resolve('url', 'run', 'host', none()), /unavailable/);
    await web.command('restart', none());
    await tunnel.command('restart', none());
    const nextUrl = await tunnel.resolve('url', 'run', 'host', none());
    assert.equal((await (await fetch(new URL('/api/health', nextUrl))).json()).status, 'healthy');
    await rm(join(directory, 'consumer.json'));
    await consumer.command('restart', none());
    await consumerResult(directory, nextUrl);
    await cache.command('restart', none());
    assert.equal(await cache.redisCommand('GET', 'ats-persistence', '', false, none()), 'passed');
    await server.command('restart', none());
    assert.equal(await database.query('SELECT value FROM ats_probe', false, none()), 'passed');
    assert.equal(await builder.publish(), before);

    const controller = new AbortController();
    const started = Date.now();
    const sleeping = database.query('SELECT pg_sleep(30)', false, controller.signal);
    const canceled = assert.rejects(sleeping, /cancel|signal|protocol operation failed/i);
    await new Promise(resolve => setTimeout(resolve, 300));
    controller.abort();
    await canceled;
    const cancellationMs = Date.now() - started;
    assert.ok(cancellationMs < 5000, `Cancellation took ${cancellationMs}ms.`);
    assert.equal(await database.query('SELECT 1', false, none()), '1');
    const stats = await builder.stats(none());
    if (process.env.NATIVE_HOSTING_EXPECT_AOT === '1') assert.equal(stats.dynamicCodeSupported, false);
    const handles = [builder, cache, server, database, web, tunnel, consumer].map(resource => resource.toJSON());
    await writeFile(join(directory, 'ats-handles.json'), JSON.stringify(handles));
    return {
        generatedAtsApis: true, realDcp: true, redisAuthenticated: true, postgresAuthenticated: true,
        quotedDatabaseChild: true, persistenceAcrossReplacement: true, deferredTunnelUrlConsumer: true,
        tunnelStopRestart: true, symbolicGraphUnchanged: true, cancellationMs,
        tunnelTransport: 'explicit-local-cli-fixture', liveRelayValidated: false,
        stats,
        guestGeneration: generation,
        containerIds: [(await cache.status(none())).containerId, (await server.status(none())).containerId],
        handles,
    };
}
