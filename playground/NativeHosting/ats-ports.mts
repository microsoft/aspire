import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';
import { AspireExport, defineIntegration } from './generated/base.mjs';
import { CancellationToken, unregisterCallback } from './generated/transport.mjs';
import type { NativeBuilder, NativeResource, ControlRequest } from './generated/aspire.mjs';
import { projections } from './generated/ports-projection.mjs';
import { TunnelOutputParser } from './devtunnel-output.mts';
import { redis, postgresQuery } from './integration-ports.mts';

type Tool = { executable: string; prefix: string[]; environment: Record<string, string>; localFixture: boolean };
type NativeValueHandle = Awaited<ReturnType<NativeBuilder['literal']>>;
type Server = { password: NativeResource };
type Database = { server: NativeResource; databaseName: string };
const servers = new Map<string, Server>();
const databases = new Map<string, Database>();
const monitors = new Map<string, { abort: AbortController; task: Promise<void> }>();
const ownedTunnels = new Set<string>();
let tool: Tool;
let dataDirectory: string;

export function configureTunnel(value: Tool): void { tool = value; }
export function configureStorage(directory: string): void { dataDirectory = directory; }
const key = (resource: NativeResource) => resource.toJSON().$handle;
const resolve = (resource: NativeResource, property: string, token = new CancellationToken()) => resource.resolve(property, 'run', 'host', token);
const tokenNone = () => new CancellationToken();
async function localOperation<T>(resource: NativeResource, token: CancellationToken, action: (token: rpc.CancellationToken) => Promise<T>): Promise<T> {
    const cancellation = new rpc.CancellationTokenSource();
    void cancellation.token;
    let checking = false;
    let check: Promise<void> | undefined;
    let failure: unknown;
    // ATS callback tokens are remote IDs, not local AbortSignals. Poll a cheap
    // generated capability while a local protocol/CLI operation is running so
    // graph disposal and cancellation kill that operation too. A push-based
    // cancellation adapter is a remaining transport dependency to extract.
    const timer = setInterval(() => {
        if (checking || cancellation.token.isCancellationRequested) return;
        checking = true;
        check = resource.status(token).then(() => {}).catch(error => {
            failure = error;
            if (!cancellation.token.isCancellationRequested) cancellation.cancel();
        }).finally(() => { checking = false; });
    }, 100);
    try {
        const result = await action(cancellation.token);
        if (failure) throw failure;
        return result;
    } finally { clearInterval(timer); await check; cancellation.dispose(); }
}

async function concat(builder: NativeBuilder, ...items: (string | NativeValueHandle)[]): Promise<NativeValueHandle> {
    return builder.concat(await Promise.all(items.map(item => typeof item === 'string' ? builder.literal(item) : item)));
}

const addRedis = AspireExport(projections.addRedis, async ({ builder, name }: { builder: NativeBuilder; name: string }) => {
    const password = await builder.addResource(`${name}-password`, 'parameter', { secret: true });
    const cache = await builder.addResource(name, 'container', {
        image: 'docker.io/library/redis:8.6', command: '/bin/sh',
        endpoints: { tcp: { scheme: 'redis', targetPort: 6379 } }, volumes: [{ name: `${name}-data`, target: '/data' }],
    });
    await cache.withArgument('-c').withArgument('exec redis-server --appendonly yes --requirepass "$REDIS_PASSWORD"');
    await cache.withEnvironment('REDIS_PASSWORD', await password.getParameter(false));
    await cache.withProperty('password', await password.getParameter(false));
    await cache.withProperty('host', await cache.getEndpoint('tcp', 'host'));
    await cache.withProperty('port', await cache.getEndpoint('tcp', 'port'));
    await cache.withProperty('uri', await concat(builder, await cache.getEndpoint('tcp', 'scheme'), '://:',
        await password.getParameter(true), '@', await cache.getEndpoint('tcp', 'host'), ':', await cache.getEndpoint('tcp', 'port')));
    await cache.withHealth(async (resource, token) => {
        try {
            const uri = await resolve(resource, 'uri', token);
            return await localOperation(resource, token, async cancellation => (await redis(uri, [['PING']], cancellation))[0] === 'PONG');
        }
        catch (error) {
            if (!(error instanceof Error) || !('code' in error) || error.code !== 'ECONNREFUSED') throw error;
            return false;
        }
    });
    return cache;
});

const addPostgres = AspireExport(projections.addPostgres, async ({ builder, name }: { builder: NativeBuilder; name: string }) => {
    if (!dataDirectory) throw new Error('A session-owned host data directory is required.');
    const source = await mkdtemp(join(dataDirectory, 'postgres-'));
    const password = await builder.addResource(`${name}-password`, 'parameter', { secret: true });
    const server = await builder.addResource(name, 'container', {
        image: 'docker.io/library/postgres:18.3', endpoints: { tcp: { scheme: 'postgresql', targetPort: 5432 } },
        // Use session-owned host storage to exercise bind-mount persistence
        // alongside Redis's named volume, without sharing another graph's data.
        bindMounts: [{ source, target: '/var/lib/postgresql' }],
    });
    await server.withEnvironment('POSTGRES_USER', 'aspire').withEnvironment('POSTGRES_PASSWORD', await password.getParameter(false))
        .withEnvironment('POSTGRES_HOST_AUTH_METHOD', 'scram-sha-256')
        .withEnvironment('POSTGRES_INITDB_ARGS', '--auth-host=scram-sha-256 --auth-local=scram-sha-256');
    await server.withProperty('uri', await concat(builder, 'postgresql://aspire:', await password.getParameter(true),
        '@', await server.getEndpoint('tcp', 'host'), ':', await server.getEndpoint('tcp', 'port'), '/postgres'));
    await server.withProperty('password', await password.getParameter(false));
    await server.withProperty('host', await server.getEndpoint('tcp', 'host'));
    await server.withProperty('port', await server.getEndpoint('tcp', 'port'));
    servers.set(key(server), { password });
    await server.withHealth(async (resource, token) => {
        try {
            const state = await resource.status(token);
            const uri = await resolve(resource, 'uri', token);
            return await localOperation(resource, token, cancellation => postgresQuery(state, uri, 'postgres', 'SELECT 1', cancellation)) === '1';
        } catch (error) {
            if (!(error instanceof Error) || !error.message.startsWith('PostgreSQL protocol operation failed')) throw error;
            console.error(error.message);
            return false;
        }
    });
    return server;
});

const addDatabase = AspireExport(projections.addDatabase, async ({ server, name, databaseName }: { server: NativeResource; name: string; databaseName: string }) => {
    const parent = servers.get(key(server));
    if (!parent) throw new Error('addDatabase requires a PostgreSQL server owned by this integration generation.');
    // The builder handle is captured when the parent is created, not reconstructed
    // from a caller-supplied name or a forged ATS type.
    const builder = serverBuilders.get(key(server));
    if (!builder) throw new Error('PostgreSQL graph owner is unavailable.');
    const database = await builder.addResource(name, 'value', {});
    await database.withParent(server).waitFor(server);
    await database.withProperty('database', databaseName);
    await database.withProperty('uri', await concat(builder, 'postgresql://aspire:', await parent.password.getParameter(true),
        '@', await server.getEndpoint('tcp', 'host'), ':', await server.getEndpoint('tcp', 'port'), '/', encodeURIComponent(databaseName)));
    await database.withInitialize(async (_, token) => {
        const state = await server.status(token);
        const uri = await resolve(server, 'uri', token);
        // CREATE DATABASE copies and syncs a template, unlike a cheap readiness
        // query. Give initialization its own bounded filesystem-work budget.
        await localOperation(server, token, cancellation => postgresQuery(state, uri, 'postgres',
            `CREATE DATABASE "${databaseName.replaceAll('"', '""')}"`, cancellation, false, 60000));
        return true;
    });
    await database.withHealth(async (_, token) => {
        const state = await server.status(token);
        const uri = await resolve(server, 'uri', token);
        return await localOperation(server, token, cancellation => postgresQuery(state, uri, databaseName, 'SELECT 1', cancellation)) === '1';
    });
    databases.set(key(database), { server, databaseName });
    return database;
});
const serverBuilders = new Map<string, NativeBuilder>();
const addPostgresTracked = AspireExport(projections.addPostgres, async (args: { builder: NativeBuilder; name: string }) => {
    const resource = await addPostgres(args);
    serverBuilders.set(key(resource), args.builder);
    return resource;
});

const addNuxt = AspireExport(projections.addNuxt, async ({ builder, name, directory, cache }: { builder: NativeBuilder; name: string; directory: string; cache: NativeResource }) => {
    const web = await builder.addResource(name, 'executable', {
        executable: process.execPath, directory, endpoints: { http: { scheme: 'http', targetPort: 3000, environment: 'PORT' } },
    });
    for (const argument of ['node_modules/nuxt/bin/nuxt.mjs', 'dev', '--no-fork', '--no-clear']) await web.withArgument(argument);
    await web.withEnvironment('NUXT_REDIS_URI', await cache.getProperty('uri'))
        .withEnvironment('NUXT_TELEMETRY_DISABLED', '1').withEnvironment('HOST', '127.0.0.1').waitFor(cache);
    await web.withProperty('url', await web.getEndpoint('http', 'url'));
    await web.withHealth(async (resource, token) => {
        const endpoint = (await resource.status(token)).endpoints?.http;
        if (!endpoint) throw new Error('Nuxt has no allocated endpoint.');
        try {
            const response = await fetch(`http://${endpoint.host}:${endpoint.port}/api/health`, { signal: AbortSignal.timeout(2000) });
            return response.ok && (await response.json()).status === 'healthy';
        } catch (error) {
            if (!(error instanceof TypeError) && !(error instanceof DOMException)) throw error;
            return false;
        }
    });
    return web;
});

async function cli(args: string[], token: CancellationToken, resource?: NativeResource): Promise<string> {
    if (!tool) throw new Error('Dev Tunnels tool configuration is required.');
    if (resource) return localOperation(resource, token, cancellation => runCli(args, cancellation));
    return runCli(args, rpc.CancellationToken.None);
}
async function runCli(args: string[], token: rpc.CancellationToken): Promise<string> {
    const child = spawn(tool.executable, [...tool.prefix, ...args], { env: { ...process.env, ...tool.environment }, stdio: 'pipe' });
    const timeout = setTimeout(() => child.kill(), 20000);
    const cancellation = token.onCancellationRequested(() => child.kill());
    let output = '';
    child.stdout.on('data', chunk => { output += chunk.toString(); if (output.length > 65536) child.kill(); });
    child.stderr.resume();
    try {
        const code = await new Promise<number | null>((fulfill, reject) => { child.once('error', reject); child.once('close', fulfill); });
        if (code !== 0 || output.length > 65536) throw new Error(`Dev Tunnels '${args[0]}' failed (${code ?? 'signal'}).`);
        return output;
    } finally { clearTimeout(timeout); cancellation.dispose(); }
}

const addDevTunnel = AspireExport(projections.addDevTunnel, async ({ builder, name, target, directory, allowAnonymous }: {
    builder: NativeBuilder; name: string; target: NativeResource; directory: string; allowAnonymous: boolean;
}) => {
    if (!tool || !allowAnonymous) throw new Error('This bounded tunnel experiment requires configured tooling and explicit anonymous access.');
    const id = `native-${randomUUID().replaceAll('-', '').slice(0, 20)}`;
    const parent = await builder.addResource(name, 'executable', { executable: tool.executable, directory, runOnly: true });
    for (const argument of [...tool.prefix, 'host', id, '--nologo']) await parent.withArgument(argument);
    for (const [name, value] of Object.entries(tool.environment)) await parent.withEnvironment(name, value);
    await parent.waitFor(target).withProperty('targetPort', await target.getEndpoint('http', 'port'));
    const port = await builder.addResource(`${name}-port`, 'custom', {
        runOnly: true, endpoints: { tunnel: { scheme: tool.localFixture ? 'http' : 'https' } },
    });
    await port.withParent(parent).waitFor(parent).withProperty('url', await port.getEndpoint('tunnel', 'url'));
    let created = false;
    let lastPort = 0;
    await parent.withBeforeStart(async (resource, token) => {
        const user = await cli(['user', 'show', '--json'], token, resource);
        if (/expired|not logged|login required/i.test(user) || typeof JSON.parse(user) !== 'object')
            throw new Error('Refresh authentication explicitly with devtunnel user login.');
        if (!created) {
            ownedTunnels.add(id);
            await cli(['create', id, '--allow-anonymous', '--expiration', '1h', '--json', '--nologo'], token, resource);
            created = true;
        } else {
            await cli(['port', 'delete', id, '--port-number', String(lastPort), '--json', '--nologo'], token, resource);
        }
        lastPort = Number(await resolve(resource, 'targetPort', token));
        await cli(['port', 'create', id, '--port-number', String(lastPort), '--protocol', 'http', '--json', '--nologo'], token, resource);
        return true;
    });
    const stopMonitor = async () => {
        const monitor = monitors.get(key(port));
        if (monitor) { monitor.abort.abort(); await monitor.task; monitors.delete(key(port)); }
    };
    await port.withControl(async (resource, request: ControlRequest, token) => {
        await stopMonitor();
        if (request.command === 'stop') { await parent.command('stop', token); return true; }
        if (request.command === 'restart') await parent.command('restart', token);
        const stdoutParser = new TunnelOutputParser(id, lastPort, tool.localFixture);
        const stderrParser = new TunnelOutputParser(id, lastPort, tool.localFixture);
        let stdoutOffset = 0, stderrOffset = 0, revision = 0;
        let endpoint: string | undefined, ready = false, executionId: string | undefined;
        const update = (state: string, callbackToken: CancellationToken, message?: string) => resource.updateCustom({
            generation: request.generation, revision: ++revision, state,
            endpoints: state === 'Healthy' && endpoint ? { tunnel: endpoint } : {}, message,
        }, callbackToken);
        const poll = async (callbackToken: CancellationToken) => {
            const state = await parent.computeStatus(callbackToken);
            if (state.state !== 'Running') throw new Error('Dev Tunnels executable is no longer running.');
            const logs = await parent.readLogs(stdoutOffset, stderrOffset, callbackToken);
            if (executionId && logs.executionId !== executionId) throw new Error('Dev Tunnels executable changed incarnation.');
            executionId = logs.executionId;
            if (logs.stdout?.offset === undefined || logs.stderr?.offset === undefined || !logs.executionId ||
                logs.stdout.data === undefined || logs.stderr.data === undefined) throw new Error('Incomplete DCP log snapshot.');
            stdoutOffset = logs.stdout.offset; stderrOffset = logs.stderr.offset;
            for (const event of [...stdoutParser.parse(Buffer.from(logs.stdout.data, 'base64').toString()),
                ...stderrParser.parse(Buffer.from(logs.stderr.data, 'base64').toString())]) {
                if (event.url) endpoint = event.url;
                if (event.ready) ready = true;
                if (event.disconnected) throw new Error('Dev Tunnels relay disconnected.');
            }
        };
        const deadline = Date.now() + 90000;
        while (!endpoint || !ready) {
            if (Date.now() >= deadline) throw new Error('Timed out awaiting the Dev Tunnels allocated endpoint.');
            await poll(token); if (!endpoint || !ready) await delay(100);
        }
        await update('Healthy', token);
        const abort = new AbortController();
        const task = (async () => {
            while (!abort.signal.aborted) {
                await delay(250);
                if (abort.signal.aborted) break;
                try { await poll(tokenNone()); }
                catch (error) {
                    if (abort.signal.aborted) break;
                    await update('Failed', tokenNone(), error instanceof Error ? error.message : 'Tunnel observation failed.');
                    break;
                }
            }
        })();
        // Keep background failures observed until cleanup joins the task. The
        // generated SDK otherwise treats an unhandled rejection as process-fatal.
        void task.catch(error => console.error('Tunnel monitor failed:', error instanceof Error ? error.message : error));
        monitors.set(key(port), { abort, task });
        return true;
    });
    return port;
});

const redisCommand = AspireExport(projections.redisCommand, async ({ resource, operation, key: name, value, wrongPassword, cancellationToken }: {
    resource: NativeResource; operation: string; key: string; value: string; wrongPassword: boolean; cancellationToken: CancellationToken;
}) => {
    const uri = new URL(await resolve(resource, 'uri', cancellationToken));
    if (wrongPassword) uri.password = 'deliberately-wrong';
    if (!['GET', 'SET', 'PING'].includes(operation)) throw new Error('Unsupported bounded Redis operation.');
    const command = operation === 'PING' ? ['PING'] : operation === 'GET' ? ['GET', name] : ['SET', name, value];
    return localOperation(resource, cancellationToken, async token => (await redis(uri.toString(), [command], token))[0] ?? '');
});
const query = AspireExport(projections.query, async ({ resource, sql, wrongPassword, cancellationToken }: {
    resource: NativeResource; sql: string; wrongPassword: boolean; cancellationToken: CancellationToken;
}) => {
    const child = databases.get(key(resource));
    if (!child) throw new Error('query requires a PostgreSQL database child from this integration generation.');
    const state = await child.server.status(cancellationToken);
    const uri = await resolve(child.server, 'uri', cancellationToken);
    return localOperation(child.server, cancellationToken, token => postgresQuery(state, uri, child.databaseName, sql, token, wrongPassword));
});
const releaseGraph = AspireExport(projections.releaseGraph, async ({ callbackIds }: { callbackIds: string[] }) => {
    for (const monitor of monitors.values()) monitor.abort.abort();
    await Promise.all([...monitors.values()].map(monitor => monitor.task));
    monitors.clear();
    for (const id of ownedTunnels) { await cli(['delete', id, '--json', '--nologo'], tokenNone()); ownedTunnels.delete(id); }
    for (const id of callbackIds) unregisterCallback(id);
    servers.clear();
    databases.clear();
    serverBuilders.clear();
    return true;
});

export const nativePorts = defineIntegration({
    name: 'NativePorts',
    capabilities: [addRedis, addPostgresTracked, addDatabase, addNuxt, addDevTunnel, redisCommand, query, releaseGraph],
});
