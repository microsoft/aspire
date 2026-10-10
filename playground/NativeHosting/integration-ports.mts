import { createConnection } from 'node:net';
import { spawn } from 'node:child_process';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';

export interface Handle { owner: string; name: string }
export type Expression = string | number | {
    kind: 'parameter' | 'endpoint' | 'property' | 'remote';
    resource: Handle;
    endpoint?: string;
    property?: string;
    format?: 'uri';
} | { kind: 'concat'; items: Expression[] };
type Definition = {
    name: string;
    kind: 'parameter' | 'container' | 'executable' | 'value';
    [key: string]: unknown;
};
type Runtime = { state: string; containerId?: string; endpoints?: Record<string, { host: string; port: number }> };

const connection = rpc.createMessageConnection(
    new rpc.StreamMessageReader(process.stdin), new rpc.StreamMessageWriter(process.stdout));
const callbacks = new Map<string, (resource: Handle, token: rpc.CancellationToken) => Promise<unknown>>();

async function core<T>(method: string, args: object, token = rpc.CancellationToken.None): Promise<T> {
    return connection.sendRequest<T>('invokeCore', { method: `model.${method}`, args }, token);
}
const define = (definition: Definition) => core<Handle>('define', { definition });
const parameter = (resource: Handle): Expression => ({ kind: 'parameter', resource });
const endpoint = (resource: Handle, property: string): Expression => ({ kind: 'endpoint', resource, endpoint: 'tcp', property });
const property = (resource: Handle, name: string): Expression => ({ kind: 'property', resource, property: name });
const concat = (...items: Expression[]): Expression => ({ kind: 'concat', items });
const formatted = (resource: Handle): Expression => ({ kind: 'parameter', resource, format: 'uri' });

async function resolve(resource: Handle, name: string, token: rpc.CancellationToken) {
    return core<string>('resolve', { resource, property: name, mode: 'run', network: 'host' }, token);
}

// This bounded RESP2 client accepts simple, error, integer and bulk replies:
// +OK\r\n, -WRONGPASS ...\r\n, :1\r\n, $3\r\nfoo\r\n, or null $-1\r\n. Parse bytes, not TCP
// chunks, and distinguish server rejection from retryable connection failures.
async function redis(uri: string, commands: string[][], token: rpc.CancellationToken): Promise<(string | null)[]> {
    const endpoint = new URL(uri);
    const socket = createConnection({ host: endpoint.hostname, port: Number(endpoint.port) });
    let buffer = Buffer.alloc(0);
    const replies: (string | null)[] = [];
    const all = [['AUTH', decodeURIComponent(endpoint.password)], ...commands];
    const cancellation = token.onCancellationRequested(() => socket.destroy(new Error('Redis request canceled.')));
    socket.setTimeout(5000, () => socket.destroy(new Error('Redis request timed out.')));
    try {
        return await new Promise<(string | null)[]>((fulfill, reject) => {
            socket.on('error', reject);
            socket.on('end', () => reject(new Error('Redis disconnected before its final reply.')));
            socket.on('connect', () => {
                for (const command of all) {
                    socket.write(`*${command.length}\r\n` + command.map(part => `$${Buffer.byteLength(part)}\r\n${part}\r\n`).join(''));
                }
            });
            socket.on('data', chunk => {
                buffer = Buffer.concat([buffer, chunk]);
                while (buffer.length) {
                    const lineEnd = buffer.indexOf('\r\n');
                    if (lineEnd < 0) return;
                    const prefix = buffer[0];
                    const line = buffer.subarray(1, lineEnd).toString();
                    if (prefix === 45) {
                        reject(new Error('Redis rejected authentication or the command.'));
                        return;
                    }
                    if (prefix === 36) {
                        const size = Number(line);
                        if (!Number.isSafeInteger(size) || size < -1) {
                            reject(new Error('Invalid Redis bulk length.'));
                            return;
                        }
                        if (size === -1) {
                            replies.push(null);
                            buffer = buffer.subarray(lineEnd + 2);
                        } else {
                            if (buffer.length < lineEnd + 4 + size) return;
                            if (buffer.subarray(lineEnd + 2 + size, lineEnd + 4 + size).toString() !== '\r\n') {
                                reject(new Error('Invalid Redis bulk terminator.'));
                                return;
                            }
                            replies.push(buffer.subarray(lineEnd + 2, lineEnd + 2 + size).toString());
                            buffer = buffer.subarray(lineEnd + 4 + size);
                        }
                    } else if (prefix === 43 || prefix === 58) {
                        replies.push(line);
                        buffer = buffer.subarray(lineEnd + 2);
                    } else {
                        reject(new Error('Unsupported RESP reply type.'));
                        return;
                    }
                    if (replies.length === all.length) {
                        fulfill(replies.slice(1));
                        return;
                    }
                }
            });
        });
    } finally {
        cancellation.dispose();
        socket.destroy();
    }
}

async function psql(parent: Handle, connectionUri: string, database: string, sql: string, token: rpc.CancellationToken, wrongPassword = false) {
    const state = await core<Runtime>('status', { resource: parent }, token);
    if (!state.containerId) throw new Error('PostgreSQL container has no runtime identity.');
    const uri = new URL(connectionUri);
    // psql is part of the pinned PostgreSQL image. This experiment delegates its
    // protocol/authentication client to integration-local tooling, not the core.
    // host.docker.internal exercises the actual DCP-allocated host port on macOS.
    // Password travels via the subprocess environment, never command arguments.
    const child = spawn('docker', [
        'exec', '--env', 'PGPASSWORD', '--env', 'PGCONNECT_TIMEOUT=5', '--env', 'PGOPTIONS=-c statement_timeout=5000', state.containerId, 'psql', '-h', 'host.docker.internal',
        '-p', uri.port, '-U', decodeURIComponent(uri.username), '-d', database,
        '-v', 'ON_ERROR_STOP=1', '-At', '-c', sql,
    ], { env: { ...process.env, PGPASSWORD: wrongPassword ? 'deliberately-wrong' : decodeURIComponent(uri.password) }, stdio: 'pipe' });
    const cancellation = token.onCancellationRequested(() => child.kill());
    const timer = setTimeout(() => child.kill(), 10000);
    let stdout = '';
    child.stdout.on('data', chunk => { stdout += chunk.toString(); });
    // Don't forward protocol errors: psql diagnostics can include identities and
    // SQL. Return explicit failure with exit status, not a success-shaped default.
    child.stderr.resume();
    try {
        const code = await new Promise<number | null>((fulfill, reject) => {
            child.once('error', reject);
            child.once('exit', fulfill);
        });
        if (code !== 0) throw new Error(`PostgreSQL protocol operation failed (${code ?? 'signal'}).`);
        return stdout.trim();
    } finally {
        clearTimeout(timer);
        cancellation.dispose();
    }
}

connection.onRequest('addRedis', async (args: { name: string }) => {
    const secret = await define({ name: `${args.name}-password`, kind: 'parameter', secret: true });
    const identity: Handle = { owner: secret.owner, name: args.name };
    const cache = await define({
        name: args.name, kind: 'container', image: 'docker.io/library/redis:8.6',
        command: '/bin/sh', arguments: ['-c', 'exec redis-server --appendonly yes --requirepass "$REDIS_PASSWORD"'],
        environment: { REDIS_PASSWORD: parameter(secret) },
        endpoints: { tcp: { scheme: 'redis', targetPort: 6379 } },
        volumes: [{ name: `${args.name}-data`, target: '/data' }],
        health: `${args.name}:health`,
        properties: {
            host: endpoint(identity, 'host'), port: endpoint(identity, 'port'), password: parameter(secret),
            uri: concat(endpoint(identity, 'scheme'), '://:', formatted(secret), '@', endpoint(identity, 'host'), ':', endpoint(identity, 'port')),
        },
    });
    const values = cache;
    callbacks.set(`${args.name}:health`, async (_, token) => {
        try {
            const result = await redis(await resolve(values, 'uri', token), [['PING']], token);
            if (result[0] !== 'PONG') throw new Error('Redis PING did not return PONG.');
            return { ready: true };
        } catch (error) {
            if (!(error instanceof Error) || !('code' in error) || error.code !== 'ECONNREFUSED') throw error;
            console.error('Redis has not accepted its allocated endpoint yet.');
            return { ready: false };
        }
    });
    return { cache, values, secret };
});

connection.onRequest('addRedisClient', async (args: { name: string; cache: Handle }) => {
    callbacks.set(`${args.name}:health`, async (_, token) => ({
        ready: (await redis(await resolve(args.cache, 'uri', token), [['GET', 'native-network-probe']], token))[0] === 'passed',
    }));
    return define({
        name: args.name, kind: 'container', image: 'docker.io/library/redis:8.6', command: '/bin/sh',
        arguments: ['-c', 'i=0; until redis-cli -h "$CACHE_HOST" -p "$CACHE_PORT" SET native-network-probe passed; do i=$((i+1)); if [ "$i" -ge 30 ]; then exit 1; fi; sleep 1; done; exec sleep 300'],
        environment: {
            CACHE_HOST: property(args.cache, 'host'), CACHE_PORT: property(args.cache, 'port'),
            REDISCLI_AUTH: property(args.cache, 'password'),
        },
        dependencies: [args.cache], health: `${args.name}:health`,
    });
});

connection.onRequest('addPostgres', async (args: { name: string; database: string; databaseName: string }) => {
    const password = await define({ name: `${args.name}-password`, kind: 'parameter', secret: true });
    const identity: Handle = { owner: password.owner, name: args.name };
    const server = await define({
        name: args.name, kind: 'container', image: 'docker.io/library/postgres:18.3',
        environment: {
            POSTGRES_USER: 'aspire', POSTGRES_PASSWORD: parameter(password),
            POSTGRES_HOST_AUTH_METHOD: 'scram-sha-256',
            POSTGRES_INITDB_ARGS: '--auth-host=scram-sha-256 --auth-local=scram-sha-256',
        },
        endpoints: { tcp: { scheme: 'postgresql', targetPort: 5432 } },
        volumes: [{ name: `${args.name}-data`, target: '/var/lib/postgresql' }],
        health: `${args.name}:health`,
        properties: {
            uri: concat(endpoint(identity, 'scheme'), '://aspire:', formatted(password), '@', endpoint(identity, 'host'), ':', endpoint(identity, 'port'), '/postgres'),
        },
    });
    const values = server;
    const database = await define({
        name: args.database, kind: 'value', parent: server, dependencies: [server],
        initialize: `${args.database}:create`, health: `${args.database}:health`,
        properties: {
            database: args.databaseName, host: endpoint(server, 'host'), port: endpoint(server, 'port'),
            uri: concat(endpoint(server, 'scheme'), '://aspire:', formatted(password), '@', endpoint(server, 'host'), ':', endpoint(server, 'port'), '/', encodeURIComponent(args.databaseName)),
        },
    });
    const query = async (db: string, sql: string, token: rpc.CancellationToken, wrong = false) => {
        const original = await resolve(values, 'uri', token);
        return psql(server, original, db, sql, token, wrong);
    };
    callbacks.set(`${args.name}:health`, async (_, token) => {
        try {
            return { ready: await query('postgres', 'SELECT 1', token) === '1' };
        } catch (error) {
            if (token.isCancellationRequested) throw error;
            if (!(error instanceof Error) || !error.message.startsWith('PostgreSQL protocol operation failed')) throw error;
            console.error('PostgreSQL has not passed its authenticated startup probe.');
            return { ready: false };
        }
    });
    callbacks.set(`${args.database}:create`, async (_, token) => {
        // Logical "app-db" can have a physical name containing a quote. SQL
        // identifiers use doubled quotes, e.g. odd"db -> CREATE DATABASE "odd""db".
        const sql = `CREATE DATABASE "${args.databaseName.replaceAll('"', '""')}"`;
        await query('postgres', sql, token);
        return { created: true };
    });
    callbacks.set(`${args.database}:health`, async (_, token) => ({ ready: await query(args.databaseName, 'SELECT 1', token) === '1' }));
    connection.onRequest(`${args.name}:query`, (a: { sql: string; wrongPassword?: boolean }, token) =>
        query(args.databaseName, a.sql, token, a.wrongPassword));
    return { server, values, database, password };
});

connection.onRequest('redisCommand', async (args: { values: Handle; commands: string[][]; wrongPassword?: boolean }, token) => {
    const uri = new URL(await resolve(args.values, 'uri', token));
    if (args.wrongPassword) uri.password = 'deliberately-wrong';
    return redis(uri.toString(), args.commands, token);
});
connection.onRequest('addNuxtPrimitive', async (args: { name: string; directory: string; reference: Handle }) => {
    callbacks.set(`${args.name}:health`, async (resource, token) => {
        const state = await core<Runtime>('status', { resource }, token);
        if (!state.endpoints?.http) throw new Error('Nuxt has no allocated HTTP endpoint.');
        const { host, port } = state.endpoints.http;
        try {
            const response = await fetch(`http://${host}:${port}/api/health`, { signal: AbortSignal.timeout(2000) });
            return { ready: response.ok && (await response.json()).status === 'healthy' };
        } catch (error) {
            if (token.isCancellationRequested) throw error;
            if (!(error instanceof TypeError) && !(error instanceof DOMException)) throw error;
            console.error('Nuxt has not passed its allocated HTTP readiness probe.');
            return { ready: false };
        }
    });
    return define({
        name: args.name, kind: 'executable', executable: process.execPath, directory: args.directory,
        arguments: ['node_modules/nuxt/bin/nuxt.mjs', 'dev', '--no-fork', '--no-clear'],
        environment: { NUXT_REDIS_URI: property(args.reference, 'uri'), NUXT_TELEMETRY_DISABLED: '1', HOST: '127.0.0.1' },
        properties: { redisUri: property(args.reference, 'uri') },
        endpoints: { http: { scheme: 'http', targetPort: 3000, environment: 'PORT' } },
        dependencies: [args.reference], health: `${args.name}:health`,
        publish: {
            buildExecutable: process.execPath, buildArguments: ['node_modules/nuxt/bin/nuxt.mjs', 'build'],
            entryPoint: '.output/server/index.mjs', outputDirectory: '.output',
        },
    });
});
connection.onRequest('invokeIntegration', async (args: { callback: string; resource: Handle }, token) => {
    const callback = callbacks.get(args.callback);
    if (!callback) throw new Error(`Unknown integration callback '${args.callback}'.`);
    return callback(args.resource, token);
});
connection.onClose(() => process.exit(0));
connection.listen();
