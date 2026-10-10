import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';
import type { Handle, Expression } from './integration-ports.mts';
import { TunnelOutputParser } from './devtunnel-output.mts';

interface Tool { executable: string; prefix: string[]; environment: Record<string, string>; localFixture: boolean }
interface Invocation { callback: string; resource: Handle; command?: 'start' | 'stop' | 'restart'; generation?: number }
interface Logs { stdout: { offset: number; data: string }; stderr: { offset: number; data: string }; executionId: string }
const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(process.stdin), new rpc.StreamMessageWriter(process.stdout));
const owner = randomUUID();
const callbacks = new Map<string, (args: Invocation, token: rpc.CancellationToken) => Promise<unknown>>();
const operations = new Set<ChildProcessWithoutNullStreams>();
const tunnels = new Set<string>();
const monitors = new Map<string, AbortController>();
const monitorCancellations = new Map<string, rpc.CancellationTokenSource>();
const monitorTasks = new Map<string, Promise<void>>();
let tool: Tool;
let closing = false;

async function core<T>(method: string, args: object, token = rpc.CancellationToken.None) {
    return connection.sendRequest<T>('invokeCore', { method: `model.${method}`, args }, token);
}
async function cli(args: string[], token = rpc.CancellationToken.None): Promise<string> {
    if (!tool) throw new Error('Configure Dev Tunnels tooling before use.');
    const child = spawn(tool.executable, [...tool.prefix, ...args], { env: { ...process.env, ...tool.environment }, stdio: 'pipe' });
    operations.add(child);
    let output = '';
    const timer = setTimeout(() => child.kill(), 20000);
    const cancellation = token.onCancellationRequested(() => child.kill());
    child.stdout.on('data', chunk => {
        output += chunk.toString();
        if (output.length > 65536) child.kill();
    });
    child.stderr.resume();
    try {
        const code = await new Promise<number | null>((fulfill, reject) => {
            child.once('error', reject);
            child.once('exit', fulfill);
        });
        if (code !== 0 || output.length > 65536) throw new Error(`Dev Tunnels '${args[0]}' failed (${code ?? 'signal'}); check CLI authentication and service access.`);
        return output;
    } finally {
        clearTimeout(timer);
        cancellation.dispose();
        operations.delete(child);
    }
}
async function preflight(token = rpc.CancellationToken.None) {
    const output = await cli(['user', 'show', '--json'], token);
    if (/expired|not logged|login required/i.test(output)) throw new Error('Dev Tunnels authentication is expired or unavailable. Run devtunnel user login explicitly.');
    let user: unknown;
    try { user = JSON.parse(output); } catch { throw new Error('Dev Tunnels did not return authenticated user JSON. Run devtunnel user login explicitly.'); }
    if (!user || typeof user !== 'object') throw new Error('Dev Tunnels authentication is unavailable.');
    return { authenticated: true, localFixture: tool.localFixture };
}

connection.onRequest('configure', (args: Tool) => {
    tool = args;
    return { controllerOwner: owner };
});
connection.onRequest('preflight', (_, token) => preflight(token));
connection.onRequest('addDevTunnel', async (args: { name: string; target: Handle; directory: string; allowAnonymous: boolean }) => {
    if (!tool) throw new Error('Configure Dev Tunnels tooling before defining resources.');
    if (!args.allowAnonymous) throw new Error('This bounded HTTP experiment requires an explicit allowAnonymous selection.');
    const tunnelId = `native-${randomUUID().replaceAll('-', '').slice(0, 20)}`;
    const targetExpression = (property: string): Expression => ({ kind: 'endpoint', resource: args.target, endpoint: 'http', property });
    const parentIdentity: Handle = { owner: args.target.owner, name: args.name };
    const parent = await core<Handle>('define', { definition: {
        name: args.name, kind: 'executable', runOnly: true, controllerOwner: owner,
        executable: tool.executable, directory: args.directory,
        arguments: [...tool.prefix, 'host', tunnelId, '--nologo'],
        environment: tool.environment, dependencies: [args.target], beforeStart: `${args.name}:provision`,
        properties: { targetPort: targetExpression('port') },
    } });
    const facadeIdentity: Handle = { owner: parent.owner, name: `${args.name}-port` };
    const facade = await core<Handle>('define', { definition: {
        name: facadeIdentity.name, kind: 'custom', runOnly: true, controllerOwner: owner,
        parent, dependencies: [parent], control: `${args.name}:control`,
        endpoints: { tunnel: { scheme: tool.localFixture ? 'http' : 'https' } },
        properties: { url: { kind: 'endpoint', resource: facadeIdentity, endpoint: 'tunnel', property: 'url' } },
    } });
    const resolvePort = (token: rpc.CancellationToken) => core<string>('resolve',
        { resource: parentIdentity, property: 'targetPort', mode: 'run' }, token).then(Number);
    let provisioned = false;
    callbacks.set(`${args.name}:provision`, async (_, token) => {
        await preflight(token);
        const port = await resolvePort(token);
        if (!provisioned) {
            // This spike deliberately creates a fresh session-owned tunnel instead
            // of adopting/reconfiguring a user's existing persistent tunnel.
            // Record the exact ID before creation to clean ambiguous CLI outcomes.
            tunnels.add(tunnelId);
            await cli(['create', tunnelId, '--allow-anonymous', '--expiration', '1h', '--json', '--nologo'], token);
            provisioned = true;
        } else {
            // Nuxt restarts can allocate a different port. Reconcile only the
            // previously owned port before starting this session's tunnel again.
            await cli(['port', 'delete', tunnelId, '--port-number', String(lastPort), '--json', '--nologo'], token);
        }
        await cli(['port', 'create', tunnelId, '--port-number', String(port), '--protocol', 'http', '--json', '--nologo'], token);
        lastPort = port;
        return { provisioned: true };
    });
    let lastPort = 0;

    async function stopMonitor() {
        monitors.get(facade.name)?.abort();
        const cancellation = monitorCancellations.get(facade.name);
        if (cancellation && !cancellation.token.isCancellationRequested) cancellation.cancel();
        await monitorTasks.get(facade.name);
        monitorCancellations.get(facade.name)?.dispose();
        monitorCancellations.delete(facade.name);
        monitors.delete(facade.name);
        monitorTasks.delete(facade.name);
    }
    callbacks.set(`${args.name}:control`, async (invocation, token) => {
        await stopMonitor();
        const generation = invocation.generation!;
        let revision = 0;
        const update = (state: string, url?: string, message?: string, requestToken = token) => core('updateCustom', {
            resource: facade, controllerOwner: owner, generation, revision: ++revision, state,
            ...(url ? { endpoints: { tunnel: url } } : {}), ...(message ? { message } : {}),
        }, requestToken);
        if (invocation.command === 'stop') {
            await core('stop', { resource: parent }, token);
            return { stopped: true };
        }
        if (invocation.command === 'restart') {
            await core('restart', { resource: parent }, token);
        }
        const port = await resolvePort(token);
        const stdoutParser = new TunnelOutputParser(tunnelId, port, tool.localFixture);
        const stderrParser = new TunnelOutputParser(tunnelId, port, tool.localFixture);
        const stdout = new TextDecoder();
        const stderr = new TextDecoder();
        let stdoutOffset = 0;
        let stderrOffset = 0;
        let endpoint: string | undefined;
        let ready = false;
        let executionId: string | undefined;
        const deadline = Date.now() + 90000;
        const poll = async (requestToken = token) => {
            const state = await core<{ state: string }>('computeStatus', { resource: parent }, requestToken);
            if (state.state !== 'Running') throw new Error('Dev Tunnels host process is no longer running.');
            const logs = await core<Logs>('readLogs', { resource: parent, stdoutOffset, stderrOffset }, requestToken);
            if (executionId && logs.executionId !== executionId) throw new Error('Dev Tunnels process incarnation changed outside the custom lifecycle operation.');
            executionId = logs.executionId;
            stdoutOffset = logs.stdout.offset;
            stderrOffset = logs.stderr.offset;
            // Stdout/stderr decoders are separate; parser inputs are newline-terminated
            // batches from each stream in the bounded CLI format.
            for (const [parser, batch] of [
                [stdoutParser, stdout.decode(Buffer.from(logs.stdout.data, 'base64'), { stream: true })],
                [stderrParser, stderr.decode(Buffer.from(logs.stderr.data, 'base64'), { stream: true })],
            ] as const) {
                for (const event of parser.parse(batch)) {
                    if (event.url) endpoint = event.url;
                    if (event.ready) ready = true;
                    if (event.disconnected) {
                        ready = false;
                        await update('Unavailable', undefined, 'Tunnel relay disconnected; public endpoint invalidated.', requestToken);
                    }
                }
            }
        };
        while (!endpoint || !ready) {
            await poll();
            if (Date.now() > deadline) throw new Error('Dev Tunnels did not report a public endpoint and readiness before timeout.');
            await delay(100);
        }
        await update('Healthy', endpoint, 'Tunnel relay ready; public endpoint allocated.');
        const abort = new AbortController();
        const monitorCancellation = new rpc.CancellationTokenSource();
        const monitorToken = monitorCancellation.token;
        monitors.set(facade.name, abort);
        monitorCancellations.set(facade.name, monitorCancellation);
        monitorTasks.set(facade.name, (async () => {
            try {
                while (!abort.signal.aborted && !closing) {
                    await delay(250, undefined, { signal: abort.signal });
                    await poll(monitorToken);
                    if (ready && endpoint) await update('Healthy', endpoint, undefined, monitorToken);
                }
            } catch (error) {
                if (abort.signal.aborted || closing) return;
                console.error('Dev Tunnels observation failed; invalidating the custom port.');
                await update('Failed', undefined, 'Tunnel host observation failed; public endpoint invalidated.');
            }
        })());
        return { ready: true };
    });
    return { parent, facade, tunnelId, controllerOwner: owner, localFixture: tool.localFixture };
});
connection.onRequest('invokeIntegration', (args: Invocation, token) => {
    const callback = callbacks.get(args.callback);
    if (!callback) throw new Error('Unknown Dev Tunnels integration callback.');
    return callback(args, token);
});
connection.onRequest('shutdown', async () => {
    closing = true;
    for (const monitor of monitors.values()) monitor.abort();
    for (const cancellation of monitorCancellations.values()) {
        if (!cancellation.token.isCancellationRequested) cancellation.cancel();
    }
    await Promise.all(monitorTasks.values());
    for (const cancellation of monitorCancellations.values()) cancellation.dispose();
    monitorCancellations.clear();
    for (const tunnel of tunnels) await cli(['delete', tunnel, '--force', '--json', '--nologo']);
    tunnels.clear();
    return { deleted: true };
});
connection.onClose(() => {
    closing = true;
    for (const monitor of monitors.values()) monitor.abort();
    for (const cancellation of monitorCancellations.values()) {
        if (!cancellation.token.isCancellationRequested) cancellation.cancel();
    }
    for (const operation of operations) operation.kill();
    // Unexpected EOF cannot promise remote deletion without working auth/service.
    // The harness explicitly calls shutdown for its fresh session-owned tunnels.
    process.exit(0);
});
connection.listen();
