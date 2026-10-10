import assert from 'node:assert/strict';
import { spawn, execFileSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { mkdtemp, mkdir, cp, symlink, readFile, writeFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import { Socket, createConnection } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';
import { AspireClient, CancellationToken, wrapIfHandle } from './generated/transport.mjs';
import { getAspireExport, type AspireIntegrationDefinition } from './generated/base.mjs';
import { createNativeBuilder } from './generated/aspire.mjs';
import { configureTunnel, configureStorage, nativePorts } from './ats-ports.mts';
import { buildAppHost, exerciseAppHost } from './ats-apphost.mts';

const directory = new URL('.', import.meta.url).pathname;
const work = await mkdtemp(join(tmpdir(), 'aspire-ats-'));
const binary = resolve(process.env.NATIVE_HOSTING_ATS_BINARY ?? 'artifacts/native-hosting/ats-server/NativeHosting.AtsServer');
const dcp = resolve(process.env.NATIVE_HOSTING_DCP ?? '/Users/davidfowler/.nuget/packages/microsoft.developercontrolplane.darwin-arm64/0.26.5/tools/dcp');
const auth = randomUUID();
const children: ChildProcessWithoutNullStreams[] = [];
const results: Record<string, unknown> = {};
const launch = (executable: string, args: string[], env: Record<string, string>) => {
    const child = spawn(executable, args, { env: { ...process.env, ...env }, stdio: 'pipe' });
    children.push(child);
    let output = '';
    if (!args.includes('--stdio')) child.stdout.on('data', chunk => { output += chunk.toString(); });
    child.stderr.on('data', chunk => { output += chunk.toString(); });
    return { child, output: () => output };
};
async function waitFor(predicate: () => Promise<boolean>, name: string, timeout = 90000) {
    const deadline = Date.now() + timeout;
    while (!await predicate()) {
        if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${name}.`);
        await delay(100);
    }
}
async function prepare(name: string) {
    const path = join(work, name);
    await mkdir(path);
    await mkdir(join(path, 'fixture'));
    await cp(join(directory, 'web'), join(path, 'web'), { recursive: true });
    await symlink(resolve(directory, '../NuxtApp/web/node_modules'), join(path, 'web/node_modules'), 'dir');
    await cp(resolve(directory, '../NuxtApp/web/package.json'), join(path, 'web/package.json'));
    return path;
}
async function exit(child: ChildProcessWithoutNullStreams, stop = false) {
    if (stop && child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
    await waitFor(async () => child.exitCode !== null || child.signalCode !== null, 'owned process exit', stop ? 30000 : 480000);
    if (!stop) assert.equal(child.exitCode, 0);
}
function assertContainersRemoved(ids: (string | null)[]) {
    for (const id of ids) {
        assert.equal(execFileSync('docker', ['ps', '-aq', '--filter', `id=${id}`], { encoding: 'utf8' }).trim(), '');
    }
}
try {
    const mockDirectory = await prepare('mock');
    const mock = launch(binary, ['--stdio'], { ASPIRE_REMOTE_APPHOST_TOKEN: auth, NATIVE_HOSTING_DCP: dcp });
    const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(mock.child.stdout), new rpc.StreamMessageWriter(mock.child.stdin));
    if (!(mock.child.stdout instanceof Socket)) throw new Error('The mock requires a socket-backed process pipe.');
    const client = AspireClient.fromConnection(connection, mock.child.stdout, () => {});
    const integration: AspireIntegrationDefinition = nativePorts;
    const exports = integration.capabilities.map(fn => ({ fn, meta: getAspireExport(fn)! }));
    connection.onRequest('getCapabilities', () => ({
        protocolVersion: 2,
        capabilities: exports.map(({ meta }) => ({ id: meta.id, method: meta.method, description: meta.description, ...meta.projection })),
    }));
    connection.onRequest('handleExternalCapability', async (id: string, args: Record<string, unknown>, invocation: string, token: rpc.CancellationToken) => {
        const export_ = exports.find(value => value.meta.id === id);
        if (!export_) throw new Error(`Unknown integration capability '${id}'.`);
        const controller = new AbortController();
        const registration = token.onCancellationRequested(() => controller.abort());
        const wrapped = wrapIfHandle(args, client) as Record<string, unknown>;
        for (const parameter of export_.meta.projection?.parameters ?? []) {
            if (parameter.type?.typeId === 'cancellationToken') wrapped[parameter.name] = CancellationToken.from(controller.signal);
        }
        try { return await export_.fn(wrapped); }
        finally { registration.dispose(); }
    });
    connection.listen();
    assert.equal(await connection.sendRequest('authenticate', auth), true);
    // A mock guest and integration host share the same parent-owned stdio pipe.
    // Socket mode below separates them using the real integration-host runtime.
    const mockBuilder = await createNativeBuilder(client);
    await connection.sendRequest('registerAsIntegrationHost', 'mock-ports');
    configureTunnel({
        executable: process.execPath, prefix: [join(directory, 'devtunnel-fixture.mts')],
        environment: { ASPIRE_TUNNEL_FIXTURE_DIR: join(mockDirectory, 'fixture') }, localFixture: true,
    });
    configureStorage(mockDirectory);
    const mockGraph = await buildAppHost(mockBuilder, mockDirectory, 'mock');
    try { results.mock = await exerciseAppHost(mockGraph, mockDirectory, 'mock'); }
    catch (error) {
        console.error(mock.output());
        const container = error instanceof Error ? /Container: ([a-f0-9]+)\./.exec(error.message)?.[1] : undefined;
        if (container) console.error(execFileSync('docker', ['logs', container], { encoding: 'utf8' }));
        await mockBuilder.releaseGraph([], new CancellationToken());
        mock.child.stdin.end();
        await exit(mock.child);
        connection.dispose();
        throw error;
    }
    await mockBuilder.releaseGraph([], new CancellationToken());
    mock.child.stdin.end();
    await exit(mock.child);
    connection.dispose();
    assertContainersRemoved((results.mock as { containerIds: string[] }).containerIds);

    const socketPath = join(work, 'apphost.sock');
    const env = { REMOTE_APP_HOST_SOCKET_PATH: socketPath, ASPIRE_REMOTE_APPHOST_TOKEN: auth, NATIVE_HOSTING_DCP: dcp };
    const server = launch(binary, [], env);
    await waitFor(async () => {
        try { return (await stat(socketPath)).isSocket(); }
        catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false; throw error; }
    }, 'native server socket');
    const socket = createConnection(socketPath);
    await new Promise<void>((fulfill, reject) => { socket.once('connect', fulfill); socket.once('error', reject); });
    const probe = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
    probe.listen();
    assert.equal(await probe.sendRequest('ping'), 'pong');
    await assert.rejects(probe.sendRequest('invokeCapability', 'NativeHosting.Ats/createNativeBuilder', {}), /Authenticate/);
    assert.equal(await probe.sendRequest('authenticate', auth), true);
    probe.onRequest('getCapabilities', () => ({
        protocolVersion: 2, capabilities: [{ id: 'NativeHosting.Ats/addRedis' }, { id: 'unknown/export' }],
    }));
    await assert.rejects(probe.sendRequest('registerAsIntegrationHost', 'invalid-host'), /known and unique/);
    const hostDirectory = join(work, 'host');
    await mkdir(hostDirectory);
    await mkdir(join(hostDirectory, 'fixture'));
    const host = launch(process.execPath, [join(directory, 'ats-integration-host.mts')], {
        ...env, ASPIRE_INTEGRATION_HOST_REGISTRATION_ID: randomUUID(),
        NATIVE_HOSTING_TUNNEL_FIXTURE: join(hostDirectory, 'fixture'),
        NATIVE_HOSTING_PORT_DATA_DIRECTORY: hostDirectory,
    });
    await waitFor(async () => host.output().includes('Registered integrations:'), 'real ATS integration-host registration');
    let previousHandles: string | undefined;
    for (const generation of [1, 2]) {
        const guestDirectory = await prepare(`guest-${generation}`);
        const guest = launch(process.execPath, [join(directory, 'ats-guest.mts')], {
            ...env, NATIVE_HOSTING_GUEST_DIRECTORY: guestDirectory,
            NATIVE_HOSTING_GUEST_GENERATION: String(generation),
            ...(previousHandles ? { NATIVE_HOSTING_STALE_HANDLES: previousHandles } : {}),
        });
        await waitFor(async () => (await probe.sendRequest<{ handles: number }>('getRuntimeState')).handles > 0, 'active guest graph');
        const duplicateSocket = createConnection(socketPath);
        await new Promise<void>((fulfill, reject) => { duplicateSocket.once('connect', fulfill); duplicateSocket.once('error', reject); });
        const duplicate = rpc.createMessageConnection(new rpc.StreamMessageReader(duplicateSocket), new rpc.StreamMessageWriter(duplicateSocket));
        duplicate.listen();
        assert.equal(await duplicate.sendRequest('authenticate', auth), true);
        const rejected = await duplicate.sendRequest<{ $error: { message: string } }>('invokeCapability', 'NativeHosting.Ats/createNativeBuilder', {});
        assert.equal(rejected.$error.message, 'Only one guest graph can be active.');
        duplicate.dispose(); duplicateSocket.destroy();
        // Disconnecting a rejected second guest must not dispose the real graph.
        try { await exit(guest.child); }
        catch (error) { console.error(guest.output(), host.output(), server.output()); throw error; }
        const result = JSON.parse(await readFile(join(guestDirectory, 'ats-result.json'), 'utf8'));
        results[`serverGeneration${generation}`] = result;
        await waitFor(async () => (await probe.sendRequest<{ idle: boolean; handles: number }>('getRuntimeState')).idle, 'native graph reset');
        await waitFor(async () => result.containerIds.every((id: string) =>
            execFileSync('docker', ['ps', '-aq', '--filter', `id=${id}`], { encoding: 'utf8' }).trim() === ''), 'graph cleanup after guest EOF');
        previousHandles = join(guestDirectory, 'ats-handles.json');
        assert.equal(server.child.exitCode, null);
        assert.equal(host.child.exitCode, null);
    }
    probe.dispose(); socket.destroy();
    await exit(host.child, true);
    await exit(server.child, true);
    assert.equal(server.child.exitCode, 0);
    results.reload = { serverPidUnchanged: true, integrationHostPidUnchanged: true, guestProcessReexecuted: true, staleHandlesRejected: true,
        rejectedGuestDisconnectPreservesGraph: true, failedIntegrationRegistrationIsAtomic: true,
        changedGuestEnvironmentObserved: true,
        policy: 'dispose old graph, controllers and session volumes; rebuild next generation', warmResourceReconciliation: false };
    console.log(JSON.stringify(results, null, 2));
} finally {
    for (const child of children.reverse()) {
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
    }
    await Promise.all(children.map(child => exit(child, true)));
    if (process.env.NATIVE_HOSTING_ATS_RESULTS) await writeFile(process.env.NATIVE_HOSTING_ATS_RESULTS, JSON.stringify(results, null, 2));
    await rm(work, { recursive: true });
}
