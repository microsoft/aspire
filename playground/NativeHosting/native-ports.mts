import assert from 'node:assert/strict';
import { spawn, execFileSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { cp, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir, homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';
import type { Handle } from './integration-ports.mts';

interface Runtime { state: string; endpoints: Record<string, { host: string; port: number }>; containerId?: string; pid?: number }
const directory = dirname(fileURLToPath(import.meta.url));
const work = await mkdtemp(join(tmpdir(), 'native-ports-'));
const binary = resolve(process.argv[2] ?? 'artifacts/native-hosting/core/NativeHosting.Core');
const dcp = process.env.NATIVE_HOSTING_DCP ?? join(homedir(), '.nuget/packages/microsoft.developercontrolplane.darwin-arm64/0.26.5/tools/dcp');
const children: ChildProcessWithoutNullStreams[] = [];
const connections: rpc.MessageConnection[] = [];
let coreProcess: ChildProcessWithoutNullStreams;
const result: Record<string, unknown> = {};

function start(executable: string, args: string[]) {
    const child = spawn(executable, args, { stdio: 'pipe' });
    children.push(child);
    child.stderr.pipe(process.stderr, { end: false });
    child.on('error', error => console.error(`Launch failed: ${error.message}`));
    return child;
}
function connect(child: ChildProcessWithoutNullStreams) {
    const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(child.stdout), new rpc.StreamMessageWriter(child.stdin));
    connections.push(connection);
    connection.onClose(() => connection.dispose());
    connection.listen();
    return connection;
}
async function request<T>(connection: rpc.MessageConnection, method: string, args: object = {}): Promise<T> {
    const cancellation = new rpc.CancellationTokenSource();
    const timer = setTimeout(() => cancellation.cancel(), 240000);
    try {
        return await connection.sendRequest<T>(method, args, cancellation.token);
    } finally {
        clearTimeout(timer);
        cancellation.dispose();
    }
}
async function waitHttp(url: string) {
    const deadline = Date.now() + 120000;
    while (Date.now() < deadline) {
        try {
            const response = await fetch(`${url}/api/health`, { signal: AbortSignal.timeout(2000) });
            if (response.ok && (await response.json()).status === 'healthy') return;
        } catch (error) {
            if (!(error instanceof TypeError) && !(error instanceof DOMException)) throw error;
        }
        await delay(250);
    }
    throw new Error('DCP-owned Nuxt did not become healthy.');
}
async function roundTrip(url: string) {
    const response = await fetch(`${url}/api/redis`, { signal: AbortSignal.timeout(10000) });
    assert.equal(response.status, 200, 'Nuxt must reach authenticated native Redis.');
    const value = await response.json();
    assert.deepEqual(value, { value: 'Nuxt read/write through an owner-resolved Redis reference', commands: 3 });
}
const dockerNames = () => execFileSync('docker', ['ps', '-a', '--format', '{{.ID}} {{.Names}}'], { encoding: 'utf8' });

async function close(child: ChildProcessWithoutNullStreams) {
    if (child.exitCode !== null || child.signalCode !== null) return;
    const exited = once(child, 'exit');
    child.stdin.end();
    const timer = setTimeout(() => child.kill(), 65000);
    try { await exited; } finally { clearTimeout(timer); }
}

async function validateModelFailures() {
    const process = start(binary, []);
    const core = connect(process);
    try {
        const anchor = await request<Handle>(core, 'model.define', { definition: { name: 'anchor', kind: 'parameter', secret: true } });
        const a = { owner: anchor.owner, name: 'a' };
        const b = { owner: anchor.owner, name: 'b' };
        await request(core, 'model.define', { definition: { name: 'a', kind: 'value', dependencies: [b], properties: { loop: { kind: 'property', resource: a, property: 'loop' } } } });
        await request(core, 'model.define', { definition: { name: 'b', kind: 'value', dependencies: [a] } });
        await assert.rejects(request(core, 'model.resolve', { resource: a, property: 'loop', mode: 'run' }), /value cycle/);
        await assert.rejects(request(core, 'model.startAll'), /dependency cycle/);
        assert.equal((await request<Runtime>(core, 'model.status', { resource: a })).state, 'NotStarted');
        result.graphCyclesFailBeforeSideEffects = true;
    } finally {
        await close(process);
    }

    const cancelProcess = start(binary, []);
    const cancelCore = connect(cancelProcess);
    try {
        let entered!: () => void;
        const rendezvous = new Promise<void>(fulfill => { entered = fulfill; });
        cancelCore.onRequest('invokeIntegration', async (_: object, token) => {
            entered();
            await new Promise<void>((_, reject) => {
                token.onCancellationRequested(() => reject(new Error('Canceled integration readiness probe.')));
                if (token.isCancellationRequested) reject(new Error('Canceled integration readiness probe.'));
            });
        });
        const blocker = await request<Handle>(cancelCore, 'model.define',
            { definition: { name: 'blocker', kind: 'value', health: 'block', properties: { literal: 'still-usable' } } });
        const consumer = await request<Handle>(cancelCore, 'model.define',
            { definition: { name: 'consumer', kind: 'value', dependencies: [blocker] } });
        const cancellation = new rpc.CancellationTokenSource();
        const starting = cancelCore.sendRequest('model.startAll', {}, cancellation.token);
        // Attach the rejection assertion before canceling the request so Node never
        // observes an unhandled rejection while the reentrant stats call completes.
        const failed = assert.rejects(starting, /canceled|cancelled/i);
        await rendezvous;
        assert.equal((await request<{ resources: number }>(cancelCore, 'model.stats')).resources, 2);
        cancellation.cancel();
        await failed;
        cancellation.dispose();
        assert.equal((await request<Runtime>(cancelCore, 'model.status', { resource: blocker })).state, 'FailedToStart');
        assert.equal((await request<Runtime>(cancelCore, 'model.status', { resource: consumer })).state, 'FailedToStart');
        assert.equal(await request(cancelCore, 'model.resolve', { resource: blocker, property: 'literal', mode: 'run' }), 'still-usable');
        result.canceledReadinessFailsDependentsAndPreservesRpc = true;
    } finally {
        await close(cancelProcess);
    }
}

function managedHelperRows(ownerPid: number) {
    // Hosting's DCP apiserver detaches, so parent ancestry alone cannot identify
    // it. Match this adapter's --monitor PID, then follow only its descendants.
    // Retain process start identity so cleanup never signals a recycled PID.
    const rows = execFileSync('ps', ['-axo', 'pid=,ppid=,lstart=,command='], { encoding: 'utf8' }).trim().split('\n').map(line => {
        const fields = line.trim().split(/\s+/);
        return { pid: Number(fields[0]), parent: Number(fields[1]), started: fields.slice(2, 7).join(' '), command: fields.slice(7).join(' ') };
    });
    const owned = new Set(rows.filter(row => row.command.includes(`--monitor ${ownerPid} `)).map(row => row.pid));
    let changed = true;
    while (changed) {
        changed = false;
        for (const row of rows) if (owned.has(row.parent) && !owned.has(row.pid)) { owned.add(row.pid); changed = true; }
    }
    return rows.filter(row => owned.has(row.pid));
}

async function mixedManagedRedis() {
    const nativeProcess = start(binary, []);
    const core = connect(nativeProcess);
    const adapter = start('dotnet', [resolve('artifacts/bin/NativeHosting.ManagedAdapter/Debug/net10.0/NativeHosting.ManagedAdapter.dll')]);
    const managed = connect(adapter);
    const hostProcess = start(process.execPath, [join(directory, 'integration-ports.mts')]);
    const integration = connect(hostProcess);
    let callbacks = 0;
    integration.onRequest('invokeCore', (args: { method: string; args: object }, token) => core.sendRequest(args.method, args.args, token));
    core.onRequest('invokeIntegration', (args: object, token) => integration.sendRequest('invokeIntegration', args, token));
    core.onRequest('resolveOwner', (args: object, token) => managed.sendRequest('resolve', args, token));
    managed.onRequest('describeConsumer', (args: object, token) => {
        callbacks++;
        return core.sendRequest('model.describe', args, token);
    });
    let helpers: ReturnType<typeof managedHelperRows> = [];
    let containerId: string | undefined;
    try {
        const cache = await request<Handle>(managed, 'addRedis', { name: 'cache' });
        const imported = await request<Handle>(core, 'model.define', { definition: {
            name: 'cache-reference', kind: 'value',
            properties: { uri: { kind: 'remote', resource: cache, property: 'Uri' } },
        } });
        const web = await request<Handle>(integration, 'addNuxtPrimitive',
            { name: 'web', directory: join(work, 'web'), reference: imported });
        await request(core, 'model.configure', { dcp });
        const canonical = await request<object>(core, 'model.publish');
        const symbolic = await request<string>(core, 'model.resolve', { resource: web, property: 'redisUri', mode: 'publish' });
        assert.equal(symbolic, '{cache.bindings.tcp.scheme}://:{cache-password.value}@{cache.bindings.tcp.host}:{cache.bindings.tcp.port}');
        await request(managed, 'start');
        helpers = managedHelperRows(adapter.pid!);
        containerId = (await request<{ containerId: string }>(managed, 'deploymentInputs', { reference: cache })).containerId;
        await request(core, 'model.startAll');
        const runtime = await request<Runtime>(core, 'model.status', { resource: web });
        const url = `http://${runtime.endpoints.http.host}:${runtime.endpoints.http.port}`;
        await roundTrip(url);
        const resolved = await Promise.all(Array.from({ length: 4 }, () =>
            request<string>(core, 'model.resolve', { resource: web, property: 'redisUri', mode: 'run' })));
        assert.equal(new Set(resolved).size, 1);
        assert.ok(callbacks >= 5, 'Generic native values must support real CLR-owner reentrant callbacks.');
        assert.deepEqual(await request(core, 'model.publish'), canonical);
        const stats = await request<{ assemblies: string[]; modelResources: string[] }>(managed, 'stats');
        assert.deepEqual(stats.modelResources, ['cache']);
        assert.ok(!stats.assemblies.includes('Aspire.Hosting.JavaScript'));
        await close(adapter);
        await assert.rejects(request(core, 'model.resolve', { resource: web, property: 'redisUri', mode: 'run' }), /disposed|closed|disconnect/i);
        await close(nativeProcess);
        assert.ok(containerId);
        // Existing Hosting retains a stopped Docker container for diagnostics.
        // Its detached DCP observes owner death asynchronously after RPC EOF.
        const deadline = Date.now() + 15000;
        while (Date.now() < deadline && execFileSync('docker', ['ps', '--filter', `id=${containerId}`, '--format', '{{.ID}}'], { encoding: 'utf8' }).trim()) {
            await delay(250);
        }
        assert.equal(execFileSync('docker', ['ps', '--filter', `id=${containerId}`, '--format', '{{.ID}}'], { encoding: 'utf8' }).trim(), '');
        if (dockerNames().includes(containerId.slice(0, 12))) {
            assert.equal(execFileSync('docker', ['inspect', '--format', '{{.State.Running}}', containerId], { encoding: 'utf8' }).trim(), 'false');
        }
        return { nuxtRedisRoundTrip: true, callbacks, managedResources: stats.modelResources,
            managedJavaScriptLoaded: false, symbolicPublicationPreserved: true, ownerDisconnectFailsResolution: true,
            cleanupPolicy: 'Managed stopped-container retention is unchanged; the harness removes its exact observed container.' };
    } finally {
        await close(nativeProcess);
        await close(adapter);
        await close(hostProcess);
        // The legacy managed adapter still uses Hosting's detached DCP launch.
        // Preserve the existing experiment's supervised helper cleanup, separate
        // from the native owner's own direct DCP cleanup evidence.
        for (const signal of ['SIGTERM', 'SIGKILL'] as const) {
            const remaining = managedHelperRows(adapter.pid!).filter(row => helpers.some(observed => observed.pid === row.pid && observed.started === row.started));
            for (const row of remaining) {
                try { process.kill(row.pid, signal); } catch (error) {
                    if (!(error instanceof Error) || !('code' in error) || error.code !== 'ESRCH') throw error;
                }
            }
            if (remaining.length) await delay(500);
        }
        assert.equal(managedHelperRows(adapter.pid!).length, 0, 'Managed owner helpers must not survive the experiment.');
        if (containerId && dockerNames().includes(containerId.slice(0, 12))) {
            execFileSync('docker', ['rm', '--force', containerId], { stdio: 'ignore' });
        }
    }
}

try {
    await validateModelFailures();
    await mkdir(join(work, 'web'));
    await cp(join(directory, 'web'), join(work, 'web'), { recursive: true });
    await symlink(resolve(directory, '../NuxtApp/web/node_modules'), join(work, 'web/node_modules'), 'dir');
    await writeFile(join(work, 'web/package.json'), await readFile(resolve(directory, '../NuxtApp/web/package.json')));
    coreProcess = start(binary, []);
    const core = connect(coreProcess);
    const integrationProcess = start(process.execPath, [join(directory, 'integration-ports.mts')]);
    const integration = connect(integrationProcess);
    integration.onRequest('invokeCore', (args: { method: string; args: object }, token) => core.sendRequest(args.method, args.args, token));
    core.onRequest('invokeIntegration', (args: object, token) => integration.sendRequest('invokeIntegration', args, token));
    core.onRequest('resolveOwner', () => { throw new Error('This native-only experiment has no managed owner.'); });

    await request(core, 'model.configure', { dcp });
    result.dcpConfigured = true;
    const redis = await request<{ cache: Handle; values: Handle; secret: Handle }>(integration, 'addRedis', { name: 'cache' });
    const containerConsumer = await request<Handle>(integration, 'addRedisClient', { name: 'redis-client', cache: redis.cache });
    const postgres = await request<{ server: Handle; values: Handle; database: Handle; password: Handle }>(integration, 'addPostgres',
        { name: 'postgres', database: 'app-db', databaseName: 'odd"db' });
    const web = await request<Handle>(integration, 'addNuxtPrimitive', { name: 'web', directory: join(work, 'web'), reference: redis.values });
    const modelBefore = await request<object>(core, 'model.publish');
    const symbolicBefore = await request<string>(core, 'model.resolve', { resource: postgres.database, property: 'uri', mode: 'publish' });
    assert.equal(symbolicBefore, '{postgres.bindings.tcp.scheme}://aspire:{postgres-password.value:uri}@{postgres.bindings.tcp.host}:{postgres.bindings.tcp.port}/odd%22db');
    await assert.rejects(request(core, 'model.define', { definition: { name: 'cache', kind: 'value' } }), /already exists/);
    await assert.rejects(request(core, 'model.resolve', { resource: { ...redis.values, owner: 'foreign' }, property: 'uri', mode: 'run' }), /identity/);
    await request(core, 'model.startAll');
    result.nativeOnly = true;
    await assert.rejects(request(core, 'model.define', { definition: { name: 'late', kind: 'value' } }), /sealed/);

    const redisRuntime = await request<Runtime>(core, 'model.status', { resource: redis.cache });
    const postgresRuntime = await request<Runtime>(core, 'model.status', { resource: postgres.server });
    const webRuntime = await request<Runtime>(core, 'model.status', { resource: web });
    const containerConsumerRuntime = await request<Runtime>(core, 'model.status', { resource: containerConsumer });
    assert.equal(redisRuntime.state, 'Healthy');
    assert.equal(postgresRuntime.state, 'Healthy');
    assert.equal((await request<Runtime>(core, 'model.status', { resource: postgres.database })).state, 'Healthy');
    assert.ok(webRuntime.pid);
    let url = `http://${webRuntime.endpoints.http.host}:${webRuntime.endpoints.http.port}`;
    await waitHttp(url);
    await roundTrip(url);
    result.dcpOwnedNuxtRedisRoundTrip = true;
    assert.equal(containerConsumerRuntime.state, 'Healthy');
    result.dcpContainerNetworkAuthenticatedWrite = true;
    await assert.rejects(request(integration, 'redisCommand', { values: redis.values, commands: [['PING']], wrongPassword: true }), /rejected/);
    await assert.rejects(request(integration, 'postgres:query', { sql: 'SELECT 1', wrongPassword: true }), /failed/);
    result.badCredentialsRejected = ['redis', 'postgresql'];

    const child = await request<string>(integration, 'postgres:query', {
        sql: "CREATE TABLE native_probe (value text); INSERT INTO native_probe VALUES ('persisted'); SELECT current_database();",
    });
    assert.equal(child.split('\n').at(-1), 'odd"db');
    result.physicalDatabaseName = 'odd"db';
    await request(integration, 'redisCommand', { values: redis.values, commands: [['SET', 'native-persist', 'persisted'], ['SAVE']] });
    const originalUri = await request<string>(core, 'model.resolve', { resource: redis.values, property: 'uri', mode: 'run' });
    const originalPg = await request<string>(core, 'model.resolve', { resource: postgres.database, property: 'uri', mode: 'run' });
    const privateSecrets = [decodeURIComponent(new URL(originalUri).password), decodeURIComponent(new URL(originalPg).password)];
    const containerUri = await request<string>(core, 'model.resolve', { resource: redis.values, property: 'uri', mode: 'run', network: 'container' });
    assert.equal(new URL(containerUri).hostname, 'cache');
    assert.equal(new URL(containerUri).port, '6379');
    result.networkContexts = ['host-allocated-port', 'container-logical-name-and-target-port'];
    const modelAfter = await request<object>(core, 'model.publish');
    assert.deepEqual(modelAfter, modelBefore, 'Execution must not contaminate symbolic definitions.');
    const modelText = JSON.stringify(modelAfter);
    for (const secret of privateSecrets) assert.ok(!modelText.includes(secret), 'Canonical publication must not include generated credentials.');
    assert.equal(await request(core, 'model.resolve', { resource: postgres.database, property: 'uri', mode: 'publish' }), symbolicBefore);
    result.symbolicPublicationPreserved = true;

    const restarting = request(core, 'model.restart', { resource: redis.cache });
    await assert.rejects(request(core, 'model.restart', { resource: redis.cache }), /already running/);
    await restarting;
    result.conflictingRestartRejected = true;
    assert.deepEqual(await request(integration, 'redisCommand', { values: redis.values, commands: [['GET', 'native-persist']] }), ['persisted']);
    await request(core, 'model.restart', { resource: postgres.server });
    assert.equal(await request(integration, 'postgres:query', { sql: 'SELECT value FROM native_probe' }), 'persisted');
    result.containerRestartPersistence = ['redis-aof', 'postgresql-child-table'];
    // Re-evaluate the native executable's environment after its provider's port
    // changes. A provider expression is deferred; an already-running process's
    // inherited environment is not dynamically rewritten.
    await request(core, 'model.restart', { resource: web });
    const restarted = await request<Runtime>(core, 'model.status', { resource: web });
    url = `http://${restarted.endpoints.http.host}:${restarted.endpoints.http.port}`;
    await waitHttp(url);
    await roundTrip(url);
    result.executableRestartRebindsProvider = true;

    const samples = [];
    for (let i = 0; i < 5; i++) {
        const listing = execFileSync('ps', ['-axo', 'pid=,ppid=,rss=,lstart=,comm='], { encoding: 'utf8' });
        const rows = listing.trim().split('\n').map(line => {
            // ps lstart has five fields: "Sat Oct 10 00:27:48 2026".
            const fields = line.trim().split(/\s+/);
            if (fields.length < 9) throw new Error('Unexpected ps process format.');
            return { pid: Number(fields[0]), parent: Number(fields[1]), rssBytes: Number(fields[2]) * 1024,
                started: fields.slice(3, 8).join(' '), command: fields.slice(8).join(' ') };
        });
        const owned = new Set(children.filter(child => child.exitCode === null && child.signalCode === null).map(child => child.pid!));
        let changed = true;
        while (changed) {
            changed = false;
            for (const row of rows) if (owned.has(row.parent) && !owned.has(row.pid)) { owned.add(row.pid); changed = true; }
        }
        samples.push(rows.filter(row => owned.has(row.pid)));
        await delay(200);
    }
    result.hostProcessSamples = samples;
    result.stats = await request(core, 'model.stats');
    const finalRedis = await request<Runtime>(core, 'model.status', { resource: redis.cache });
    const finalPostgres = await request<Runtime>(core, 'model.status', { resource: postgres.server });
    const liveContainers = [finalRedis.containerId, finalPostgres.containerId, containerConsumerRuntime.containerId];
    result.containers = liveContainers;
    result.model = modelBefore;
    await writeFile(join(work, 'native-model.json'), JSON.stringify(modelBefore, null, 2));

    // Closing RPC stdin causes the AOT core to dispose its DCP-owned workloads
    // while the external integration is still alive, then exit.
    const exited = once(coreProcess, 'exit');
    coreProcess.stdin.end();
    const timer = setTimeout(() => coreProcess.kill(), 60000);
    try {
        const [code] = await exited;
        assert.equal(code, 0, 'Native owner EOF must clean up before successful exit.');
    } finally { clearTimeout(timer); }
    for (const id of liveContainers) assert.ok(id && !dockerNames().includes(id!.slice(0, 12)), 'Latest owned containers must be deleted.');
    const remainingProcesses = new Map(execFileSync('ps', ['-axo', 'pid=,lstart='], { encoding: 'utf8' }).trim().split('\n').map(line => {
        const fields = line.trim().split(/\s+/);
        return [Number(fields[0]), fields.slice(1).join(' ')] as const;
    }));
    for (const observed of samples.at(-1)!) {
        if (observed.pid !== integrationProcess.pid) {
            assert.notEqual(remainingProcesses.get(observed.pid), observed.started, 'A native-owned workload/helper survived EOF.');
        }
    }
    const networks = execFileSync('docker', ['network', 'ls', '--format', '{{.Name}}'], { encoding: 'utf8' });
    assert.ok(!networks.includes(`native-${redis.cache.owner.slice(0, 10)}`));
    const volumes = execFileSync('docker', ['volume', 'ls', '--format', '{{.Name}}'], { encoding: 'utf8' });
    assert.ok(!volumes.includes(`native-${redis.cache.owner.slice(0, 10)}`));
    result.ownerEofCleanup = true;
    result.mixedManagedRedis = await mixedManagedRedis();
    result.claim = 'Bounded native Redis, PostgreSQL child and Nuxt execution through real DCP without a managed adapter, plus unchanged managed Redis over owner-routed values. Not full integration ports or a performance comparison.';
    const output = process.env.NATIVE_HOSTING_RESULTS;
    if (output) await writeFile(output, JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
} finally {
    for (const child of children) {
        await close(child);
    }
    for (const connection of connections) connection.dispose();
    await rm(work, { recursive: true, force: true });
}
