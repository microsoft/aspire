import assert from 'node:assert/strict';
import { spawn, execFileSync, spawnSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { cp, copyFile, mkdir, mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { createServer } from 'node:net';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';

interface ResourceHandle {
    owner: string;
    name: string;
}
interface ExecutableDescription {
    owner: string;
    name: string;
    directory: string;
    executable: string;
    arguments: string[];
    environment: Record<string, string | { reference: ResourceHandle; property: string }>;
    publish: { buildExecutable: string; buildArguments: string[]; entryPoint: string; outputDirectory: string };
}
interface CoreStats {
    dynamicCodeSupported: boolean;
    workingSetBytes: number;
    resources: number;
}
interface ManagedStats {
    workingSetBytes: number;
    assemblies: string[];
    redisAssembly: string;
    modelResources: string[];
}
interface DeploymentInputs {
    containerId: string;
    name: string;
    targetPort: number;
    passwordParameter: string;
}

const directory = dirname(fileURLToPath(import.meta.url));
const repository = resolve(directory, '../..');
const coreBinary = resolve(process.argv[2] ?? join(repository, 'artifacts/native-hosting/core/NativeHosting.Core'));
const adapterAssembly = resolve(process.argv[3] ??
    join(repository, 'artifacts/bin/NativeHosting.ManagedAdapter/Debug/net10.0/NativeHosting.ManagedAdapter.dll'));
const output = process.env.NATIVE_HOSTING_RESULTS;
const work = await mkdtemp(join(tmpdir(), 'native-hosting-'));
const children: ChildProcessWithoutNullStreams[] = [];
const connections: rpc.MessageConnection[] = [];
const observedProcesses = new Map<number, string>();

function start(executable: string, args: string[], options: { cwd?: string; env?: NodeJS.ProcessEnv } = {}) {
    const child = spawn(executable, args, { ...options, stdio: 'pipe' });
    children.push(child);
    child.stderr.pipe(process.stderr, { end: false });
    // Install listeners before any exit, including executable-not-found.
    child.on('error', error => process.stderr.write(`Child launch failed: ${error.message}\n`));
    return child;
}

function connect(child: ChildProcessWithoutNullStreams) {
    const connection = rpc.createMessageConnection(
        new rpc.StreamMessageReader(child.stdout),
        new rpc.StreamMessageWriter(child.stdin),
    );
    connections.push(connection);
    connection.onClose(() => connection.dispose());
    connection.listen();
    return connection;
}

async function request<T>(connection: rpc.MessageConnection, method: string, args: object): Promise<T> {
    const cancellation = new rpc.CancellationTokenSource();
    const timer = setTimeout(() => cancellation.cancel(), 180000);
    try {
        return await connection.sendRequest<T>(method, args, cancellation.token);
    } finally {
        clearTimeout(timer);
        cancellation.dispose();
    }
}

async function waitHealthy(url: string) {
    const deadline = Date.now() + 180000;
    while (Date.now() < deadline) {
        try {
            const response = await fetch(`${url}/api/health`, { signal: AbortSignal.timeout(2000) });
            if (response.ok && (await response.json()).status === 'healthy') return;
        } catch (error) {
            if (!(error instanceof TypeError) && !(error instanceof DOMException)) throw error;
        }
        await delay(250);
    }
    throw new Error('Nuxt did not become healthy within three minutes.');
}

async function verifyRoundTrip(url: string) {
    const response = await fetch(`${url}/api/redis`, { signal: AbortSignal.timeout(15000) });
    const text = await response.text();
    assert.equal(response.status, 200, text.slice(0, 500));
    const result = JSON.parse(text);
    assert.equal(result.value, 'Nuxt read/write through an owner-resolved Redis reference');
    assert.equal(result.commands, 3, 'AUTH, SET and GET must all reach Redis');
}

async function command(executable: string, args: string[], cwd: string) {
    const child = start(executable, args, { cwd, env: { ...process.env, NUXT_TELEMETRY_DISABLED: '1' } });
    child.stdout.pipe(process.stderr, { end: false });
    const [code] = await once(child, 'exit');
    assert.equal(code, 0, `Command failed: ${executable}`);
}

async function verifyContainerContext(plan: ExecutableDescription, managed: rpc.MessageConnection, cache: ResourceHandle, symbolic: string) {
    const inputs = await request<DeploymentInputs>(managed, 'deploymentInputs', { reference: cache });
    const name = `native-hosting-${work.split('/').at(-1)!.toLowerCase()}`;
    const image = `${name}:test`;
    const webName = `${name}-web`;
    let attached = false;
    const runtimeUri = plan.environment.NUXT_REDIS_URI;
    assert.ok(typeof runtimeUri === 'string');
    const password = new URL(runtimeUri).password;
    assert.ok(password);
    const bindings = new Map([
        [`${inputs.name}.bindings.tcp.scheme`, 'redis'],
        [`${inputs.name}.bindings.tcp.host`, inputs.name],
        [`${inputs.name}.bindings.tcp.port`, String(inputs.targetPort)],
        [`${inputs.passwordParameter}.value`, '${CACHE_PASSWORD}'],
    ]);
    // Deliberately limited target lowering, not a general manifest publisher:
    // redis://:{cache-password.value}@{cache.bindings.tcp.host}:{cache.bindings.tcp.port}
    // becomes redis://:${CACHE_PASSWORD}@cache:6379. Unknown tokens fail explicitly.
    const publishedUri = symbolic.replace(/\{([^{}]+)\}/g, (_, token: string) => {
        const value = bindings.get(token);
        if (value === undefined) throw new Error(`Unsupported deployment reference token '${token}'.`);
        return value;
    });
    assert.ok(publishedUri.includes(`@${inputs.name}:${inputs.targetPort}`));
    assert.ok(publishedUri.includes('${CACHE_PASSWORD}'));
    const recipe = {
        environment: { NUXT_REDIS_URI: publishedUri },
        entryPoint: plan.publish.entryPoint,
        externalReference: { resource: inputs.name, targetPort: inputs.targetPort },
    };
    await writeFile(join(work, 'deployment-recipe.json'), JSON.stringify(recipe, null, 2));
    try {
        await command('docker', ['build', '--quiet', '--tag', image, plan.directory], directory);
        await command('docker', ['network', 'create', name], directory);
        await command('docker', ['network', 'connect', '--alias', inputs.name, name, inputs.containerId], directory);
        attached = true;
        const container = start('docker', [
            'run', '--detach', '--name', webName, '--network', name,
            '--publish', '127.0.0.1::3000', '--env', 'NUXT_REDIS_URI', image,
        ], {
            env: { ...process.env, NUXT_REDIS_URI: publishedUri.replace('${CACHE_PASSWORD}', password) },
        });
        container.stdout.pipe(process.stderr, { end: false });
        const [code] = await once(container, 'exit');
        assert.equal(code, 0);
        const ports = JSON.parse(execFileSync('docker', ['inspect', '--format', '{{json .NetworkSettings.Ports}}', webName]).toString());
        const port: string = ports['3000/tcp'][0].HostPort;
        assert.match(port, /^\d+$/);
        const url = `http://127.0.0.1:${port}`;
        await waitHealthy(url);
        await verifyRoundTrip(url);
        return { containerRoundTrip: 'passed', publishedRedisAddress: `${inputs.name}:${inputs.targetPort}`, secretExternalized: true };
    } finally {
        if (spawnSync('docker', ['container', 'inspect', webName], { stdio: 'ignore' }).status === 0) {
            await command('docker', ['rm', '--force', webName], directory);
        }
        if (attached) await command('docker', ['network', 'disconnect', name, inputs.containerId], directory);
        if (spawnSync('docker', ['network', 'inspect', name], { stdio: 'ignore' }).status === 0) {
            await command('docker', ['network', 'rm', name], directory);
        }
        if (spawnSync('docker', ['image', 'inspect', image], { stdio: 'ignore' }).status === 0) {
            await command('docker', ['image', 'rm', image], directory);
        }
    }
}

// Account for only this harness and its descendants. Redis runs in Docker's VM,
// whose memory is outside host-process RSS; do not present this as total-system RAM.
function processTree() {
    const rows = processRows();
    const owned = new Set([process.pid]);
    // DCP intentionally detaches its apiserver. Parent ancestry alone misses both
    // it and the workloads it launches. Identify this session by its monitor PID.
    const activeChildren = children.filter(child => child.exitCode === null && child.signalCode === null && child.pid);
    for (const row of rows) {
        if (activeChildren.some(child => row.command.includes(`--monitor ${child.pid} `))) owned.add(row.pid);
    }
    let changed = true;
    while (changed) {
        changed = false;
        for (const row of rows) {
            if (owned.has(row.parent) && !owned.has(row.pid)) {
                owned.add(row.pid);
                changed = true;
            }
        }
    }
    // Exclude the short-lived `ps` measurement child.
    const result = rows.filter(row => owned.has(row.pid) && (row.parent !== process.pid || children.some(child => child.pid === row.pid)));
    for (const row of result) {
        if (row.pid !== process.pid) observedProcesses.set(row.pid, row.started);
    }
    return result;
}

function processRows() {
    return execFileSync('ps', ['-axo', 'pid=,ppid=,rss=,state=,lstart=,command=']).toString().trim().split('\n')
        .map(line => {
            const fields = line.trim().split(/\s+/);
            return {
                pid: Number(fields[0]), parent: Number(fields[1]), rss: Number(fields[2]),
                state: fields[3], started: fields.slice(4, 9).join(' '), command: fields.slice(9).join(' '),
            };
        }).filter(row => !row.state.startsWith('Z'));
}

async function verifyCleanup() {
    for (let attempt = 0; attempt < 20; attempt++) {
        const remaining = processRows().filter(row => observedProcesses.get(row.pid) === row.started);
        if (remaining.length === 0) return;
        await delay(250);
    }
    throw new Error('A process observed in this session survived graceful shutdown.');
}

async function cleanupRemainingProcesses() {
    // Re-check start time before signaling: a recycled PID is not our process.
    const signaled = new Set<number>();
    for (const signal of ['SIGTERM', 'SIGKILL'] as const) {
        const remaining = processRows().filter(row => observedProcesses.get(row.pid) === row.started);
        for (const row of remaining) {
            try {
                process.kill(row.pid, signal);
                signaled.add(row.pid);
            } catch (error) {
                if (!(error instanceof Error) || !('code' in error) || error.code !== 'ESRCH') throw error;
            }
        }
        if (remaining.length) await delay(1000);
    }
    await verifyCleanup();
    if (signaled.size) process.stderr.write(`Harness supervision reaped ${signaled.size} remaining owned process(es).\n`);
    return signaled.size;
}

async function sampleMemory() {
    const samples: number[] = [];
    for (let index = 0; index < 5; index++) {
        samples.push(processTree().reduce((total, row) => total + row.rss, 0) * 1024);
        await delay(1000);
    }
    return { medianHostTreeRssBytes: samples.toSorted((a, b) => a - b)[2], samples };
}

async function stopChild(child: ChildProcessWithoutNullStreams) {
    if (child.exitCode !== null || child.signalCode !== null || !child.pid) return;
    processTree();
    const exited = once(child, 'exit');
    child.kill('SIGTERM');
    const timer = setTimeout(() => child.kill('SIGKILL'), 10000);
    try {
        await exited;
    } finally {
        clearTimeout(timer);
    }
}

async function runSplit() {
    const began = performance.now();
    const coreProcess = start(coreBinary, []);
    const managedProcess = start('dotnet', [adapterAssembly]);
    const hostProcess = start(process.execPath, [join(directory, 'nuxt-integration.mts')]);
    const core = connect(coreProcess);
    const managed = connect(managedProcess);
    const host = connect(hostProcess);
    let callbacks = 0;
    let pauseResolution = false;
    core.onRequest('describeOwner', (args, cancellation) => managed.sendRequest('describe', args, cancellation));
    core.onRequest('resolveOwner', (args, cancellation) => managed.sendRequest(pauseResolution ? 'delay' : 'resolve', args, cancellation));
    managed.onRequest('describeConsumer', (args, cancellation) => {
        callbacks++;
        return core.sendRequest('describe', args, cancellation);
    });
    host.onRequest('invokeCore', ({ method, args }, cancellation) => core.sendRequest(method, args, cancellation));

    const initial = await request<CoreStats>(core, 'stats', {});
    assert.equal(initial.dynamicCodeSupported, false, 'The core must actually be Native AOT');
    const cache = await request<ResourceHandle>(managed, 'addRedis', { name: 'cache' });
    const web = await request<ResourceHandle>(host, 'addNuxt', { name: 'web', directory: join(work, 'web') });
    await request(host, 'withReference', { resource: web, reference: cache });
    await assert.rejects(request(host, 'addNuxt', { name: 'WEB', directory: join(work, 'web') }), /already exists/);
    await assert.rejects(request(core, 'describe', { resource: { ...web, owner: 'another-session' } }), /invalid/);
    await assert.rejects(request(host, 'withReference', { resource: web, reference: { ...cache, owner: 'another-session' } }), /invalid/);
    await assert.rejects(request(core, 'resolveEnvironment', { resource: web, mode: 'unexpected' }), /Mode must/);

    // Publish must work before endpoint allocation and must never persist a password.
    const publishBefore = await request<ExecutableDescription>(core, 'resolveEnvironment', { resource: web, mode: 'publish' });
    assert.ok(typeof publishBefore.environment.NUXT_REDIS_URI === 'string');
    const symbolic = publishBefore.environment.NUXT_REDIS_URI;
    assert.ok(symbolic.includes('{cache.bindings.tcp.'));
    assert.ok(symbolic.includes('{cache-password.value}'));
    await request(managed, 'start', {});
    const plans = await Promise.all(Array.from({ length: 4 }, () =>
        request<ExecutableDescription>(core, 'resolveEnvironment', { resource: web, mode: 'run' })));
    assert.equal(callbacks, 4, 'Reentrant cross-owner callbacks must complete');
    const plan = plans[0];
    const runtimeUri = plan.environment.NUXT_REDIS_URI;
    assert.ok(typeof runtimeUri === 'string');
    assert.equal(new URL(runtimeUri).protocol, 'redis:');
    const description = await request<ExecutableDescription>(core, 'describe', { resource: web });
    assert.equal(typeof description.environment.NUXT_REDIS_URI, 'object', 'Native state must retain the deferred owner reference');
    const publishAfter = await request<ExecutableDescription>(core, 'resolveEnvironment', { resource: web, mode: 'publish' });
    assert.equal(publishAfter.environment.NUXT_REDIS_URI, symbolic, 'Run resolution must not contaminate publish values');

    const environment: NodeJS.ProcessEnv = { ...process.env, HOST: '127.0.0.1', NUXT_TELEMETRY_DISABLED: '1' };
    for (const [key, value] of Object.entries(plan.environment)) {
        assert.ok(typeof value === 'string');
        environment[key] = value;
    }
    // Nuxt dev chooses an available port if the default is occupied. Reserve an
    // ephemeral port immediately before launch; production endpoint allocation is
    // intentionally not implemented by this feasibility harness.
    const { port } = await allocatePort();
    environment.PORT = String(port);
    const dev = start(plan.executable, plan.arguments, { cwd: plan.directory, env: environment });
    dev.stdout.pipe(process.stderr, { end: false });
    const url = `http://127.0.0.1:${port}`;
    await waitHealthy(url);
    await verifyRoundTrip(url);
    const startupMs = performance.now() - began;
    const memory = await sampleMemory();
    const coreStats = await request<CoreStats>(core, 'stats', {});
    const managedStats = await request<ManagedStats>(managed, 'stats', {});
    assert.equal(coreStats.resources, 1);
    assert.deepEqual(managedStats.modelResources, ['cache']);
    assert.equal(managedStats.assemblies.includes('Aspire.Hosting.JavaScript'), false);
    await stopChild(dev);

    await command(plan.publish.buildExecutable, plan.publish.buildArguments, plan.directory);
    const production = start(plan.executable, [plan.publish.entryPoint], { cwd: plan.directory, env: environment });
    production.stdout.pipe(process.stderr, { end: false });
    await waitHealthy(url);
    await verifyRoundTrip(url);
    await stopChild(production);
    const deployment = await verifyContainerContext(plan, managed, cache, symbolic);

    pauseResolution = true;
    const cancellation = new rpc.CancellationTokenSource();
    const pending = core.sendRequest('resolveEnvironment', { resource: web, mode: 'run' }, cancellation.token);
    setTimeout(() => cancellation.cancel(), 100);
    await assert.rejects(pending, /canceled|cancelled/i);
    cancellation.dispose();
    pauseResolution = false;
    await request(core, 'resolveEnvironment', { resource: web, mode: 'run' });
    await request(managed, 'stop', {});
    processTree();
    pauseResolution = true;
    const interrupted = request(core, 'resolveEnvironment', { resource: web, mode: 'publish' });
    await delay(100);
    managedProcess.stdin.end();
    await assert.rejects(interrupted, /closed|disposed|disconnected|canceled|cancelled/i);
    for (const connection of [host, managed, core]) connection.dispose();
    for (const child of [hostProcess, managedProcess, coreProcess]) child.stdin.end();
    await Promise.all([hostProcess, managedProcess, coreProcess].map(async child => {
        const [code] = child.exitCode === null ? await once(child, 'exit') : [child.exitCode];
        assert.equal(code, 0, 'Integration processes must exit cleanly after EOF');
    }));
    const supervisedProcessesReaped = await cleanupRemainingProcesses();
    return {
        startupMs, ...memory, coreStats,
        managedStats: {
            workingSetBytes: managedStats.workingSetBytes, assemblyCount: managedStats.assemblies.length,
            redisAssembly: managedStats.redisAssembly,
            javaScriptIntegrationLoaded: managedStats.assemblies.includes('Aspire.Hosting.JavaScript'),
            modelResources: managedStats.modelResources,
        },
        callbacks, run: 'passed', nitroProduction: 'passed', publishReferences: 'passed',
        cancellation: 'passed', ownerDisconnect: 'passed', cleanup: 'passed', supervisedProcessesReaped, deployment,
    };
}

async function allocatePort(): Promise<{ port: number }> {
    const server = createServer();
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const address = server.address();
    assert.ok(address && typeof address !== 'string');
    const port = address.port;
    await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    return { port };
}

async function runManagedBaseline() {
    const began = performance.now();
    const child = start('dotnet', [adapterAssembly]);
    const managed = connect(child);
    await request(managed, 'addRedis', { name: 'cache' });
    await request(managed, 'addNuxtBaseline', { directory: join(work, 'web') });
    const url = await request<string>(managed, 'start', {});
    await verifyRoundTrip(url);
    const startupMs = performance.now() - began;
    const memory = await sampleMemory();
    const stats = await request<ManagedStats>(managed, 'stats', {});
    assert.ok(stats.modelResources.includes('web'), 'The all-managed comparison must really model Nuxt in CLR Hosting');
    processTree();
    await request(managed, 'stop', {});
    managed.dispose();
    child.stdin.end();
    const [code] = await once(child, 'exit');
    assert.equal(code, 0);
    const supervisedProcessesReaped = await cleanupRemainingProcesses();
    return {
        startupMs, ...memory,
        stats: { workingSetBytes: stats.workingSetBytes, assemblyCount: stats.assemblies.length }, supervisedProcessesReaped,
        caveat: 'Direct managed Hosting baseline, not the full current ATS server; not a parity/performance claim.',
    };
}

try {
    // Reuse the already-restored Nuxt 4 fixture without changing dependency manifests.
    await mkdir(join(work, 'web'), { recursive: true });
    await copyFile(join(repository, 'playground/NuxtApp/web/package.json'), join(work, 'web/package.json'));
    await cp(join(directory, 'web'), join(work, 'web'), { recursive: true });
    await symlink(join(repository, 'playground/NuxtApp/web/node_modules'), join(work, 'web/node_modules'), 'dir');
    const split = await runSplit();
    const managedBaseline = await runManagedBaseline();
    const results = { split, managedBaseline };
    if (output) await writeFile(output, JSON.stringify(results, null, 2));
    console.log(JSON.stringify(results, null, 2));
} finally {
    for (const connection of connections) connection.dispose();
    for (const child of children.toReversed()) await stopChild(child);
    await cleanupRemainingProcesses();
    await rm(work, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
}
