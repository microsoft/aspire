import assert from 'node:assert/strict';
import { spawn, execFileSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir, homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';
import type { Handle } from './integration-ports.mts';
import { TunnelOutputParser } from './devtunnel-output.mts';

interface Runtime {
    state: string; generation: number; revision: number; pid?: number; logs?: string[];
    endpoints?: Record<string, { url: string; host: string; port: number }>;
}
interface Invocation { callback: string; resource: Handle; command?: string; generation?: number }
const directory = dirname(fileURLToPath(import.meta.url));
const work = await mkdtemp(join(tmpdir(), 'native-tunnel-'));
const binary = resolve(process.argv[2] ?? 'artifacts/native-hosting/core/NativeHosting.Core');
const dcp = process.env.NATIVE_HOSTING_DCP ?? join(homedir(), '.nuget/packages/microsoft.developercontrolplane.darwin-arm64/0.26.5/tools/dcp');
const live = process.env.NATIVE_HOSTING_LIVE_TUNNEL === '1';
const children: ChildProcessWithoutNullStreams[] = [];
const connections: rpc.MessageConnection[] = [];
const result: Record<string, unknown> = { transport: live ? 'real-dev-tunnels-service' : 'local-cli-protocol-fixture', liveRelayValidated: false };

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
    const timer = setTimeout(() => cancellation.cancel(), 180000);
    try { return await connection.sendRequest<T>(method, args, cancellation.token); }
    finally { clearTimeout(timer); cancellation.dispose(); }
}
async function close(child: ChildProcessWithoutNullStreams) {
    if (child.exitCode !== null || child.signalCode !== null) return;
    const exited = once(child, 'exit');
    child.stdin.end();
    const timer = setTimeout(() => child.kill(), 60000);
    try { await exited; } finally { clearTimeout(timer); }
}
async function waitState(core: rpc.MessageConnection, resource: Handle, state: string) {
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
        const status = await request<Runtime>(core, 'model.status', { resource });
        if (status.state === state) return status;
        await delay(100);
    }
    throw new Error(`Custom resource did not enter ${state}.`);
}
async function waitResult(path: string) {
    const deadline = Date.now() + 20000;
    while (Date.now() < deadline) {
        try { return JSON.parse(await readFile(path, 'utf8')); } catch (error) {
            if (!(error instanceof Error) || !('code' in error) || error.code !== 'ENOENT') throw error;
        }
        await delay(100);
    }
    throw new Error('Deferred URL consumer did not produce its forwarded HTTP result.');
}

function parserChecks() {
    const parser = new TunnelOutputParser('abc', 7007);
    assert.deepEqual(parser.parse('Hosting port: 7007\nConnect via browser: https://abc-'), [{}]);
    const parsed = parser.parse('7007.usw2.devtunnels.ms/\nInspect network activity: https://abc-7007-inspect.usw2.devtunnels.ms/\n\x1b[32mReady to accept connections for tunnel: abc.usw2\x1b[0m\nConnection to host tunnel relay closed.\n');
    assert.equal(parsed[0].url, 'https://abc-7007.usw2.devtunnels.ms/');
    assert.equal(parsed[2].ready, true);
    assert.equal(parsed[3].disconnected, true);
    assert.throws(() => new TunnelOutputParser('abc', 7007).parse('Hosting port 7008 at https://abc-7008.usw2.devtunnels.ms/\n'), /unexpected/);
    assert.throws(() => new TunnelOutputParser('abc', 7007).parse('Hosting port 7007 at https://example.com/\n'), /invalid/);
    assert.throws(() => new TunnelOutputParser('abc', 7007).parse('Hosting port 7007 at http://127.0.0.1:7007/\n'), /invalid/);
    assert.throws(() => new TunnelOutputParser('abc', 7007).parse('Ready to accept connections for tunnel: foreign.usw2\n'), /foreign/);
    assert.throws(() => new TunnelOutputParser('abc', 7007).parse('x'.repeat(16385)), /bounded/);
    result.boundedOutputParsing = true;
}

async function customContractChecks() {
    const process = start(binary, []);
    const core = connect(process);
    const controllerOwner = 'contract-test-owner';
    let resource: Handle;
    let commandCount = 0;
    core.onRequest('invokeIntegration', async (invocation: Invocation, token) => {
        commandCount++;
        if (invocation.command === 'stop') return null;
        if (commandCount === 3 || commandCount === 5) {
            await new Promise<void>((_, reject) => token.onCancellationRequested(() => reject(new Error('Controller command canceled.'))));
            return null;
        }
        await request(core, 'model.updateCustom', {
            resource, controllerOwner, generation: invocation.generation, revision: 1,
            state: 'Healthy', endpoints: { tunnel: 'https://example.test/' },
        });
        return null;
    });
    try {
        // Use a parameter only to obtain this core session's handle identity.
        const anchor = await request<Handle>(core, 'model.define', { definition: { name: 'anchor', kind: 'parameter' } });
        const identity = { owner: anchor.owner, name: 'custom' };
        resource = await request(core, 'model.define', { definition: {
            name: 'custom', kind: 'custom', runOnly: true, controllerOwner, control: 'control',
            endpoints: { tunnel: { scheme: 'https' } },
            properties: { url: { kind: 'endpoint', resource: identity, endpoint: 'tunnel', property: 'url' } },
        } });
        await request(core, 'model.startAll');
        const update = { resource, controllerOwner, generation: 1, revision: 2, state: 'Healthy', endpoints: { tunnel: 'https://example.test/' } };
        const duplicates = await Promise.allSettled(Array.from({ length: 4 }, () => request(core, 'model.updateCustom', update)));
        assert.equal(duplicates.filter(value => value.status === 'fulfilled').length, 1);
        await assert.rejects(request(core, 'model.updateCustom', { ...update, controllerOwner: 'foreign', revision: 3 }), /owner|stale/);
        await assert.rejects(request(core, 'model.updateCustom', { ...update, generation: 0, revision: 3 }), /generation|stale/);
        await assert.rejects(request(core, 'model.updateCustom', { ...update, revision: 3, endpoints: { tunnel: 'https://user:password@example.test/' } }), /invalid/i);
        await request(core, 'model.command', { resource, command: 'stop' });
        await assert.rejects(request(core, 'model.resolve', { resource, property: 'url', mode: 'run' }), /unavailable/);
        const cancellation = new rpc.CancellationTokenSource();
        const restarting = core.sendRequest('model.command', { resource, command: 'restart' }, cancellation.token);
        const failure = assert.rejects(restarting, /canceled|cancelled/i);
        const deadline = Date.now() + 5000;
        while (commandCount < 3 && Date.now() < deadline) await delay(10);
        assert.equal(commandCount, 3);
        await assert.rejects(request(core, 'model.command', { resource, command: 'restart' }), /already running/);
        cancellation.cancel();
        await failure;
        cancellation.dispose();
        assert.equal((await request<Runtime>(core, 'model.status', { resource })).state, 'Failed');
        await assert.rejects(request(core, 'model.updateCustom', { ...update, generation: 3, revision: 10 }), /stale/);
        await request(core, 'model.command', { resource, command: 'restart' });
        const restartingAfterDisconnect = request(core, 'model.command', { resource, command: 'restart' });
        const disconnectedFailure = assert.rejects(restartingAfterDisconnect, /disconnect|cancel/i);
        const disconnectDeadline = Date.now() + 5000;
        while (commandCount < 5 && Date.now() < disconnectDeadline) await delay(10);
        assert.equal(commandCount, 5);
        await request(core, 'model.ownerDisconnected', { controllerOwner });
        await disconnectedFailure;
        assert.equal((await request<Runtime>(core, 'model.status', { resource })).state, 'OwnerDisconnected');
        await assert.rejects(request(core, 'model.updateCustom', { ...update, generation: 5, revision: 100 }), /stale/);
        assert.ok((await request<{ resources: object[] }>(core, 'model.publish')).resources.length === 1);
        result.customContract = { duplicateUpdatesAccepted: 1, foreignOwnersRejected: true, staleIncarnationsRejected: true,
            invalidUrlsRejected: true, conflictingCommandsRejected: true, cancellationRevokesEndpoint: true,
            ownerDisconnectCancelsInflightCommand: true };
    } finally { await close(process); }
}

let core: rpc.MessageConnection | undefined;
let tunnelHost: rpc.MessageConnection | undefined;
let tunnelProcess: ChildProcessWithoutNullStreams | undefined;
let controllerOwner: string | undefined;
let disconnected = false;
try {
    parserChecks();
    await customContractChecks();
    const nativeProcess = start(binary, []);
    core = connect(nativeProcess);
    const nuxtHost = connect(start(process.execPath, [join(directory, 'integration-ports.mts')]));
    tunnelProcess = start(process.execPath, [join(directory, 'devtunnel-integration.mts')]);
    tunnelHost = connect(tunnelProcess);
    const relay = (args: { method: string; args: object }, token: rpc.CancellationToken) => core!.sendRequest(args.method, args.args, token);
    nuxtHost.onRequest('invokeCore', relay);
    tunnelHost.onRequest('invokeCore', relay);
    core.onRequest('invokeIntegration', (args: Invocation, token) =>
        (args.callback.startsWith('tunnel:') ? tunnelHost! : nuxtHost).sendRequest('invokeIntegration', args, token));
    core.onRequest('resolveOwner', () => { throw new Error('This custom-resource experiment has no managed owner.'); });
    await mkdir(join(work, 'fixture'));
    const tool = live ? {
        executable: process.env.NATIVE_HOSTING_DEVTUNNEL ?? execFileSync('which', ['devtunnel'], { encoding: 'utf8' }).trim(),
        prefix: [], environment: {}, localFixture: false,
    } : {
        executable: process.execPath, prefix: [join(directory, 'devtunnel-fixture.mts')],
        environment: { ASPIRE_TUNNEL_FIXTURE_DIR: join(work, 'fixture') }, localFixture: true,
    };
    controllerOwner = (await request<{ controllerOwner: string }>(tunnelHost, 'configure', tool)).controllerOwner;
    // Fail before launching Nuxt/DCP or changing remote resources when live auth
    // is unavailable. No auto-login, credential copying, or interactive fallback.
    result.phase = 'authentication-preflight';
    await request(tunnelHost, 'preflight');
    result.phase = 'resource-lifecycle';
    await cp(join(directory, 'web'), join(work, 'web'), { recursive: true });
    await symlink(resolve(directory, '../NuxtApp/web/node_modules'), join(work, 'web/node_modules'), 'dir');
    await writeFile(join(work, 'web/package.json'), await readFile(resolve(directory, '../NuxtApp/web/package.json')));
    const reference = await request<Handle>(core, 'model.define', { definition: {
        name: 'health-only-reference', kind: 'value', properties: { uri: 'redis://127.0.0.1:1' },
    } });
    const web = await request<Handle>(nuxtHost, 'addNuxtPrimitive', { name: 'web', directory: join(work, 'web'), reference });
    const tunnel = await request<{ parent: Handle; facade: Handle; tunnelId: string }>(tunnelHost, 'addDevTunnel',
        { name: 'tunnel', target: web, directory: work, allowAnonymous: true });
    const consumerResult = join(work, 'consumer.json');
    const consumer = await request<Handle>(core, 'model.define', { definition: {
        name: 'tunnel-consumer', kind: 'executable', runOnly: true, executable: process.execPath, directory: work,
        arguments: [join(directory, 'tunnel-consumer.mts')], dependencies: [tunnel.facade],
        environment: {
            TUNNEL_URL: { kind: 'property', resource: tunnel.facade, property: 'url' }, TUNNEL_RESULT: consumerResult,
        },
    } });
    const publishedBefore = await request<{ resources: { name: string }[] }>(core, 'model.publish');
    assert.deepEqual(publishedBefore.resources.map(resource => resource.name), ['health-only-reference', 'web']);
    await assert.rejects(request(core, 'model.resolve', { resource: tunnel.facade, property: 'url', mode: 'publish' }), /Run-only/);
    await request(core, 'model.configure', { dcp });
    await request(core, 'model.startAll');
    const forwarded = await waitResult(consumerResult);
    assert.equal(forwarded.body.status, 'healthy');
    assert.equal(forwarded.status, 200);
    const first = await request<Runtime>(core, 'model.status', { resource: tunnel.facade });
    assert.equal(first.state, 'Healthy');
    assert.equal(first.pid, undefined, 'The custom port must not create a DCP executable.');
    assert.ok(first.logs?.some(message => message.includes('allocated')));
    const initialUrl = await request<string>(core, 'model.resolve', { resource: tunnel.facade, property: 'url', mode: 'run' });
    assert.equal(forwarded.endpoint, initialUrl);
    assert.deepEqual(await request(core, 'model.publish'), publishedBefore);
    result.deferredUrlConsumerForwardsNativeNuxt = true;
    result.runOnlyPublicationExcluded = true;
    result.customPortHasNoExecutable = true;

    await request(core, 'model.command', { resource: tunnel.facade, command: 'stop' });
    await assert.rejects(request(core, 'model.resolve', { resource: tunnel.facade, property: 'url', mode: 'run' }), /unavailable/);
    await request(core, 'model.restart', { resource: web });
    await request(core, 'model.command', { resource: tunnel.facade, command: 'restart' });
    const next = await request<Runtime>(core, 'model.status', { resource: tunnel.facade });
    assert.ok(next.generation > first.generation);
    await assert.rejects(request(core, 'model.updateCustom', {
        resource: tunnel.facade, controllerOwner, generation: first.generation, revision: first.revision + 100,
        state: 'Healthy', endpoints: { tunnel: initialUrl },
    }), /stale/);
    const nextUrl = await request<string>(core, 'model.resolve', { resource: tunnel.facade, property: 'url', mode: 'run' });
    await rm(consumerResult);
    await request(core, 'model.restart', { resource: consumer });
    assert.equal((await waitResult(consumerResult)).endpoint, nextUrl);
    result.restartReconcilesTargetAndRebindsConsumer = true;

    const hostState = await request<{ pid: number }>(core, 'model.computeStatus', { resource: tunnel.parent });
    assert.ok(hostState.pid > 0);
    process.kill(hostState.pid, 'SIGTERM');
    await waitState(core, tunnel.facade, 'Failed');
    await assert.rejects(request(core, 'model.resolve', { resource: tunnel.facade, property: 'url', mode: 'run' }), /unavailable/);
    result.hostExitRevokesEndpoint = true;
    await request(core, 'model.command', { resource: tunnel.facade, command: 'restart' });
    await request(tunnelHost, 'shutdown');
    await close(tunnelProcess);
    assert.equal(tunnelProcess.exitCode, 0, 'Tunnel integration must exit cleanly on EOF.');
    await request(core, 'model.ownerDisconnected', { controllerOwner });
    disconnected = true;
    await waitState(core, tunnel.facade, 'OwnerDisconnected');
    await assert.rejects(request(core, 'model.resolve', { resource: tunnel.facade, property: 'url', mode: 'run' }), /unavailable/);
    await assert.rejects(request(core, 'model.command', { resource: tunnel.facade, command: 'restart' }), /disconnect/);
    assert.equal((await request<{ dynamicCodeSupported: boolean }>(core, 'model.stats')).dynamicCodeSupported, false);
    result.ownerEofInvalidatesResourceAndStopsTunnelProcess = true;
    await close(nativeProcess);
    assert.equal(nativeProcess.exitCode, 0, 'Native core must clean up its DCP workloads before exiting.');
    if (!live) assert.deepEqual(await readdir(join(work, 'fixture')), []);
    result.liveRelayValidated = live;
    result.remoteLifetime = 'Fresh experiment-owned tunnel deleted explicitly; shipped persistent tunnel semantics are unchanged.';
    result.phase = 'complete';
    if (process.env.NATIVE_HOSTING_RESULTS) await writeFile(process.env.NATIVE_HOSTING_RESULTS, JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
} catch (error) {
    result.failure = result.phase === 'authentication-preflight'
        ? 'Live Dev Tunnels authentication preflight failed; no tunnel, DCP, or Nuxt resource was created.'
        : 'Custom-resource experiment failed; inspect the explicit operation error in stderr.';
    if (process.env.NATIVE_HOSTING_RESULTS) await writeFile(process.env.NATIVE_HOSTING_RESULTS, JSON.stringify(result, null, 2));
    throw error;
} finally {
    try {
        if (tunnelHost && tunnelProcess?.exitCode === null) {
            await request(tunnelHost, 'shutdown');
        }
    } finally {
        try {
            if (core && controllerOwner && !disconnected) {
                await request(core, 'model.ownerDisconnected', { controllerOwner });
            }
        } finally {
            try {
                await Promise.all(children.map(close));
            } finally {
                for (const connection of connections) connection.dispose();
                await rm(work, { recursive: true, force: true });
            }
        }
    }
}
