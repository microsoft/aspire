import assert from 'node:assert/strict';
import { spawn, execFileSync, spawnSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { cp, copyFile, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
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

async function verifyCleanup(retained: ReadonlySet<number>) {
    for (let attempt = 0; attempt < 20; attempt++) {
        const remaining = processRows().filter(row => !retained.has(row.pid) && observedProcesses.get(row.pid) === row.started);
        if (remaining.length === 0) return;
        await delay(250);
    }
    throw new Error('A process observed in this session survived graceful shutdown.');
}

async function cleanupRemainingProcesses(keepAlive: ChildProcessWithoutNullStreams[] = []) {
    // The edge experiment deliberately keeps the leaf native core alive across
    // managed-owner shutdowns. It must not be reaped with the stopped owner's DCP.
    const retained = new Set(keepAlive.flatMap(child => child.pid ? [child.pid] : []));
    // Re-check start time before signaling: a recycled PID is not our process.
    const signaled = new Set<number>();
    for (const signal of ['SIGTERM', 'SIGKILL'] as const) {
        const remaining = processRows().filter(row => !retained.has(row.pid) && observedProcesses.get(row.pid) === row.started);
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
    await verifyCleanup(retained);
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

interface EdgeDescription {
    clrType: string;
    isExecutable: boolean;
    isJavaScript: boolean;
    relationships: string[];
    waits: string[];
    consumerWaits: string[];
    graphCallbackRan: boolean;
    callbackCalls: number;
}

async function runEdges() {
    const coreProcess = start(coreBinary, []);
    const core = connect(coreProcess);
    const nativeDefinition = {
        name: 'web', directory: join(work, 'web'), executable: process.execPath,
        arguments: ['.output/server/index.mjs'],
        publish: { buildExecutable: process.execPath, buildArguments: ['node_modules/nuxt/bin/nuxt.mjs', 'build'],
            entryPoint: '.output/server/index.mjs', outputDirectory: '.output' },
    };
    const native = await request<ResourceHandle>(core, 'addExecutable', nativeDefinition);
    await assert.rejects(request(core, 'applyEnvironment', { resource: native, environment: { BAD: 42 } }), /Invalid environment value/);
    await assert.rejects(request(core, 'applyEnvironment', { resource: native, environment: { BAD: { property: 'Uri' } } }), /reference/);
    const resultPath = join(work, 'managed-consumer-result.json');
    const importArgs = {
        resource: native, node: process.execPath, directory: work,
        consumerScript: join(directory, 'managed-consumer.mts'), resultPath,
    };
    const managedProcess = start('dotnet', [adapterAssembly]);
    const managed = connect(managedProcess);
    managed.onRequest('describeNative', (args, cancellation) => core.sendRequest('describe', args, cancellation));
    core.onRequest('resolveOwner', (args, cancellation) => managed.sendRequest('resolve', args, cancellation));
    await request(managed, 'addRedis', { name: 'cache' });
    const imported = await request<EdgeDescription>(managed, 'importNative', importArgs);
    assert.equal(imported.isExecutable, false);
    assert.equal(imported.isJavaScript, false);
    assert.deepEqual(imported.waits, ['cache']);
    assert.deepEqual(imported.consumerWaits, ['web']);
    assert.ok(imported.relationships.includes('Reference:cache'));
    const exportEnvironment = async () => {
        const environment = await request<object>(managed, 'edgeExport', {});
        await request(core, 'applyEnvironment', { resource: native, environment });
    };
    await exportEnvironment();
    const symbolicBefore = await request<ExecutableDescription>(core, 'resolveEnvironment', { resource: native, mode: 'publish' });
    assert.ok(typeof symbolicBefore.environment.NUXT_REDIS_URI === 'string');
    assert.ok(symbolicBefore.environment.NUXT_REDIS_URI.includes('{cache-password.value}'));
    // Managed startup can await a consumer's native endpoint. Start both owners
    // concurrently rather than waiting for the whole managed island first.
    const started = request(managed, 'start', {}).then(() => null, error => error as Error);
    await request(managed, 'edgeWaitDependencies', {});
    await request(managed, 'edgeBeforeStart', {});
    await exportEnvironment();
    const graph = await request<EdgeDescription>(managed, 'edgeDescribe', {});
    assert.equal(graph.graphCallbackRan, true);
    // Production gathering caches callbacks. Exporting again should not repeat
    // user side effects even though deferred providers remain re-resolvable.
    await exportEnvironment();
    const gatheredAgain = await request<EdgeDescription>(managed, 'edgeDescribe', {});
    assert.equal(gatheredAgain.callbackCalls, graph.callbackCalls);
    const plan = await request<ExecutableDescription>(core, 'resolveEnvironment', { resource: native, mode: 'run' });
    assert.equal(plan.environment.MANAGED_GRAPH, 'visible-in-before-start');
    assert.equal(plan.environment.MANAGED_LITERAL, 'configured-through-CSharp');
    assert.equal(plan.environment.MANAGED_CALLBACK, 'web');
    assert.equal(plan.environment.MANAGED_BEFORE_RESOURCE, 'before-native-launch');
    assert.ok(typeof plan.environment.CACHE_URI === 'string', 'Real WithReference must splat Redis properties');
    assert.equal(plan.environment.CACHE_URI, plan.environment.NUXT_REDIS_URI);
    assert.ok(typeof plan.environment.ConnectionStrings__cache === 'string');
    assert.equal(plan.environment['ConnectionStrings__my-db'], plan.environment.ConnectionStrings__my_db);
    await assert.rejects(request(managed, 'edgeConflict', {}), /both use.*ConnectionStrings__my_db/);
    const localNetwork = await request<{ host: string; port: number }>(managed, 'edgeNetwork', { network: 'localhost' });
    const containerNetwork = await request<{ host: string; port: number }>(managed, 'edgeNetwork', { network: 'container' });
    assert.ok(['localhost', '127.0.0.1'].includes(localNetwork.host));
    assert.equal(containerNetwork.port, 6379);
    assert.notEqual(containerNetwork.host, localNetwork.host);
    const unresolved = await request<ExecutableDescription>(core, 'describe', { resource: native });
    assert.equal(typeof unresolved.environment.CACHE_URI, 'object');
    assert.equal(typeof unresolved.environment.ConnectionStrings__cache, 'object');

    const environment: NodeJS.ProcessEnv = { ...process.env, HOST: '127.0.0.1', NUXT_TELEMETRY_DISABLED: '1' };
    for (const [key, value] of Object.entries(plan.environment)) {
        assert.ok(typeof value === 'string');
        environment[key] = value;
    }
    const launch = async () => {
        const { port } = await allocatePort();
        const child = start(plan.executable, plan.arguments, { cwd: plan.directory, env: { ...environment, PORT: String(port) } });
        child.stdout.pipe(process.stderr, { end: false });
        const url = `http://127.0.0.1:${port}`;
        await waitHealthy(url);
        return { child, port, url };
    };
    // The real managed consumer must still be waiting before the owner announces
    // readiness. It will be launched by DCP, not manually by this harness.
    await assert.rejects(readFile(resultPath), /ENOENT/);
    const first = await launch();
    const bridgeResponse = await fetch(`${first.url}/api/bridge`, { signal: AbortSignal.timeout(15000) });
    assert.equal(bridgeResponse.status, 200);
    assert.deepEqual(await bridgeResponse.json(), {
        literal: 'configured-through-CSharp', graph: 'visible-in-before-start',
        beforeResource: 'before-native-launch', callback: 'web',
    });
    await request(managed, 'edgeState', { resource: native, generation: 1, state: 'Running', port: first.port });
    const startupError = await started;
    if (startupError) throw startupError;
    await request(managed, 'edgeWaitHealthy', {});
    await request(managed, 'edgeWaitConsumer', {});
    const model = await request<ManagedStats>(managed, 'stats', {});
    assert.deepEqual(model.modelResources.toSorted(), ['cache', 'managed-consumer', 'web']);
    const nativeStats = await request<CoreStats>(core, 'stats', {});
    assert.equal(nativeStats.resources, 1, 'The facade must not create a second native workload');
    const consumer = JSON.parse(await readFile(resultPath, 'utf8')) as { url: string; serviceDiscovery: string; redisRoundTrip: string };
    assert.equal(consumer.url, first.url);
    assert.equal(consumer.serviceDiscovery, first.url);
    assert.equal(consumer.redisRoundTrip, 'passed');
    const commandResult = await request<{ success: boolean }>(managed, 'edgeCommand', {});
    assert.equal(commandResult.success, true);
    await assert.rejects(request(managed, 'edgeState', { resource: native, generation: 1, state: 'Running', port: first.port }), /Stale/);
    await assert.rejects(request(managed, 'edgeState', {
        resource: { ...native, owner: 'old-session' }, generation: 2, state: 'Running', port: first.port,
    }), /foreign/);
    await request(managed, 'edgeState', { resource: native, generation: 2, state: 'Exited' });
    await stopChild(first.child);
    const second = await launch();
    await request(managed, 'edgeState', { resource: native, generation: 3, state: 'Running', port: second.port });
    await request(managed, 'edgeWaitHealthy', {});
    const endpoints = await request<{ held: string; fresh: string; state: string }>(managed, 'edgeEndpoints', {});
    assert.equal(endpoints.fresh, second.url);
    assert.equal(endpoints.held, second.url, 'A previously captured EndpointReference must use the latest owner allocation');
    assert.equal(endpoints.state, 'Running');
    await verifyRoundTrip(endpoints.held);
    const repeatedState = await Promise.allSettled(Array.from({ length: 4 }, () =>
        request(managed, 'edgeState', { resource: native, generation: 4, state: 'Running', port: second.port })));
    assert.equal(repeatedState.filter(result => result.status === 'fulfilled').length, 1);
    for (const result of repeatedState) {
        if (result.status === 'rejected') assert.match(String(result.reason), /Stale/);
    }
    const symbolicAfter = await request<ExecutableDescription>(core, 'resolveEnvironment', { resource: native, mode: 'publish' });
    assert.equal(symbolicAfter.environment.NUXT_REDIS_URI, symbolicBefore.environment.NUXT_REDIS_URI);
    await stopChild(second.child);
    coreProcess.stdin.end();
    const [coreExit] = await once(coreProcess, 'exit');
    assert.equal(coreExit, 0);
    const cacheUriExpression = unresolved.environment.CACHE_URI;
    assert.ok(typeof cacheUriExpression === 'object');
    const providerArgs = {
        reference: cacheUriExpression.reference,
        property: 'CACHE_URI', consumer: native, mode: 'run',
    };
    await assert.rejects(request(managed, 'resolve', providerArgs), /closed|disposed|disconnected/);
    await request(managed, 'edgeInvalidate', {});
    await assert.rejects(request(managed, 'resolve', providerArgs), /invalidated/);
    // Built-in endpoint providers retain the last allocation: demonstrate the
    // lifecycle hole instead of claiming owner invalidation fixes arbitrary CLR reads.
    const invalidated = await request<{ held: string; state: string }>(managed, 'edgeEndpoints', {});
    assert.equal(invalidated.held, second.url);
    assert.equal(invalidated.state, 'FailedToStart');
    processTree();
    await request(managed, 'stop', {});
    managed.dispose();
    managedProcess.stdin.end();
    const [exit] = await once(managedProcess, 'exit');
    assert.equal(exit, 0);
    await cleanupRemainingProcesses();

    const publishCoreProcess = start(coreBinary, []);
    const publishCore = connect(publishCoreProcess);
    const publishNative = await request<ResourceHandle>(publishCore, 'addExecutable', nativeDefinition);
    assert.notEqual(publishNative.owner, native.owner);
    await assert.rejects(request(publishCore, 'describe', { resource: native }), /invalid/);
    const publish = async (bridgePublisher: boolean) => {
        const manifestPath = join(work, `edge-manifest-${bridgePublisher}.json`);
        const child = start('dotnet', [adapterAssembly, '--publisher', 'manifest', '--output-path', manifestPath]);
        const connection = connect(child);
        connection.onRequest('describeNative', (args, cancellation) => publishCore.sendRequest('describe', args, cancellation));
        await request(connection, 'addRedis', { name: 'cache' });
        await request(connection, 'importNative', { ...importArgs, resource: publishNative, bridgePublisher });
        await request(connection, 'start', {});
        const manifest = JSON.parse(await readFile(manifestPath, 'utf8')) as {
            resources: Record<string, { error?: string; type?: string; env?: Record<string, string>; bindings?: object; build?: object }>;
        };
        if (output) await copyFile(manifestPath, join(dirname(output), `native-edge-manifest-${bridgePublisher}.json`));
        processTree();
        await request(connection, 'stop', {});
        connection.dispose();
        child.stdin.end();
        const [code] = await once(child, 'exit');
        assert.equal(code, 0);
        await cleanupRemainingProcesses([publishCoreProcess]);
        return manifest;
    };
    const unsupported = await publish(false);
    assert.equal(unsupported.resources.web.error, 'This resource does not support generation in the manifest.');
    const bridged = await publish(true);
    assert.equal(bridged.resources.web.type, 'native-executable.v0');
    // The real manifest writer externalizes URI-encoded parameters as formatted
    // resources. Returning ValueExpression alone misses this publisher dependency.
    assert.equal(bridged.resources.web.env?.NUXT_REDIS_URI,
        symbolicBefore.environment.NUXT_REDIS_URI.replace('{cache-password.value}', '{cache-password-uri-encoded.value}'));
    assert.ok(bridged.resources.web.bindings);
    assert.ok(bridged.resources.web.build);
    assert.equal(bridged.resources['managed-consumer'].env?.NATIVE_URL, '{web.bindings.http.url}');
    assert.equal(bridged.resources['managed-consumer'].env?.services__web__http__0, '{web.bindings.http.url}');
    assert.ok(bridged.resources['cache-password']);
    assert.deepEqual(bridged.resources['cache-password-uri-encoded'], {
        type: 'annotated.string', value: '{cache-password.value}', filter: 'uri',
    });
    const manifestText = JSON.stringify(bridged);
    assert.equal(manifestText.includes(first.url), false);
    assert.equal(manifestText.includes(second.url), false);
    assert.ok(typeof plan.environment.NUXT_REDIS_URI === 'string');
    assert.equal(manifestText.includes(new URL(plan.environment.NUXT_REDIS_URI).password), false);
    publishCore.dispose();
    publishCoreProcess.stdin.end();
    const [publishCoreExit] = await once(publishCoreProcess, 'exit');
    assert.equal(publishCoreExit, 0);
    const failedPublisher = start('dotnet', [adapterAssembly, '--publisher', 'manifest',
        '--output-path', join(work, 'edge-manifest-owner-dead.json')]);
    const failedConnection = connect(failedPublisher);
    failedConnection.onRequest('describeNative', (args, cancellation) => publishCore.sendRequest('describe', args, cancellation));
    await request(failedConnection, 'addRedis', { name: 'cache' });
    await request(failedConnection, 'importNative', { ...importArgs, resource: publishNative, bridgePublisher: true });
    await assert.rejects(request(failedConnection, 'start', {}), /Publishing did not complete successfully/);
    failedConnection.dispose();
    failedPublisher.stdin.end();
    const [failedPublisherExit] = await once(failedPublisher, 'exit');
    assert.equal(failedPublisherExit, 0, 'The RPC reports a publish failure without crashing the adapter');
    return {
        managedToNativeReference: 'passed', nativeToManagedServiceDiscovery: consumer,
        deferredClrProviders: 'passed', graphEnumerationAndMutation: {
            callbackRan: graph.graphCallbackRan, modelResources: model.modelResources,
            environmentApplied: plan.environment.MANAGED_GRAPH,
        },
        nativeBeforeStartCallbackInjection: 'verified inside the real Nuxt process over HTTP',
        concreteTypeCompatibility: { clrType: imported.clrType, isExecutable: imported.isExecutable, isJavaScript: imported.isJavaScript },
        referenceRelationships: imported.relationships,
        productionCallbackCaching: { callsAfterGather: graph.callbackCalls, callsAfterSecondGather: gatheredAgain.callbackCalls },
        portableConnectionNamesAndConflictValidation: 'passed', sourceNetworkContext: { localNetwork, containerNetwork },
        managedReadinessAndWaits: 'passed', managedCommandRouting: 'passed', restartEndpointRebinding: 'passed',
        staleGenerationRejection: 'passed', foreignSessionRejection: 'passed', guardedOwnerInvalidation: 'passed',
        concurrentStateFencing: 'one accepted, three duplicate revisions rejected',
        unguardedEndpointAfterOwnerDeath: 'retains last URL; requires a production invalidation contract',
        defaultManifest: unsupported.resources.web, bridgedManifest: 'passed; experimental native-executable.v0, not a deployment target',
        formattedSecretDependency: 'cache-password-uri-encoded emitted by the real manifest writer',
        publishOwnerFailure: 'explicit RPC failure, not a successful partial manifest',
    };
}

async function runNativeOnly() {
    const coreProcess = start(coreBinary, []);
    const hostProcess = start(process.execPath, [join(directory, 'nuxt-integration.mts')]);
    const core = connect(coreProcess);
    const host = connect(hostProcess);
    host.onRequest('invokeCore', ({ method, args }, cancellation) => core.sendRequest(method, args, cancellation));
    const web = await request<ResourceHandle>(host, 'addNuxt', { name: 'web', directory: join(work, 'web') });
    const plan = await request<ExecutableDescription>(core, 'resolveEnvironment', { resource: web, mode: 'run' });
    const { port } = await allocatePort();
    const workload = start(plan.executable, [plan.publish.entryPoint], {
        cwd: plan.directory, env: { ...process.env, HOST: '127.0.0.1', PORT: String(port) },
    });
    workload.stdout.pipe(process.stderr, { end: false });
    await waitHealthy(`http://127.0.0.1:${port}`);
    const rows = processTree();
    assert.deepEqual(rows.filter(row => row.command.includes('NativeHosting.ManagedAdapter.dll')), [],
        'A native-only session must not launch a managed adapter');
    const stats = await request<CoreStats>(core, 'stats', {});
    assert.equal(stats.dynamicCodeSupported, false);
    assert.equal(stats.resources, 1);
    const memory = await sampleMemory();
    await stopChild(workload);
    for (const connection of [host, core]) connection.dispose();
    for (const child of [hostProcess, coreProcess]) {
        child.stdin.end();
        const [code] = await once(child, 'exit');
        assert.equal(code, 0);
    }
    await cleanupRemainingProcesses();
    return {
        health: 'passed', managedAdapters: 0, ...memory,
        caveat: 'Health-only Nuxt workload without Redis. Not a parity comparison with mixed sessions.',
    };
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
    let results: object;
    if (process.env.NATIVE_HOSTING_EDGES_ONLY === '1') {
        await command(process.execPath, ['node_modules/nuxt/bin/nuxt.mjs', 'build'], join(work, 'web'));
        results = { edges: await runEdges(), nativeOnly: await runNativeOnly() };
    } else {
        const split = await runSplit();
        const edges = await runEdges();
        const nativeOnly = await runNativeOnly();
        const managedBaseline = await runManagedBaseline();
        results = { split, edges, nativeOnly, managedBaseline };
    }
    if (output) await writeFile(output, JSON.stringify(results, null, 2));
    console.log(JSON.stringify(results, null, 2));
} finally {
    for (const connection of connections) connection.dispose();
    for (const child of children.toReversed()) await stopChild(child);
    await cleanupRemainingProcesses();
    await rm(work, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
}
