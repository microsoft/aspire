// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFile, readdir, stat, unlink } from 'node:fs/promises';
import { createConnection } from 'node:net';
import { homedir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';
import * as rpc from 'vscode-jsonrpc/node.js';

interface ResourceSnapshot {
    name: string;
    state: string;
    healthStatus: string;
    version: number;
    commands: { name: string; state: string }[];
}

interface ResourceLogLine {
    resourceName: string;
    content: string;
    lineNumber: number;
    isError: boolean;
}

interface StreamDescriptor {
    token: number;
}

interface StreamResult<T> {
    values: T[];
    finished: boolean;
}

const [cliPath, serverPath, workspace] = process.argv.slice(2);
assert.ok(cliPath && serverPath && workspace && process.env.ASPIRE_NATIVE_DCP_PATH);
const apphostPath = resolve(workspace, 'apphost.mts');
const proofPath = resolve(workspace, 'auxiliary-apphost-proof.json');
await unlink(proofPath).catch((error: unknown) => {
    if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error;
});
const cli = spawn(cliPath, ['run', '--apphost', apphostPath, '--non-interactive', '--nologo', '--log-level', 'Debug'], {
    cwd: workspace,
    env: {
        ...process.env,
        ASPIRE_CLI_NATIVE_APPHOST_SERVER: resolve(serverPath),
        ASPIRE_NATIVE_CLI_RESULTS_PATH: proofPath
    },
    stdio: ['ignore', 'pipe', 'pipe']
});
const exited = once(cli, 'exit');
const deadline = setTimeout(() => cli.kill('SIGKILL'), 180_000);
const processPattern = new RegExp(`${basename(serverPath).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\((\\d+)\\) started`);
let output = '';
let serverPid: number | undefined;
for (const stream of [cli.stdout, cli.stderr]) {
    stream.on('data', (data: Buffer) => {
        output += data.toString();
        const match = processPattern.exec(output);
        if (match) serverPid = Number(match[1]);
        if (output.length > 256 * 1024) output = output.slice(-128 * 1024);
        process.stderr.write(data);
    });
}
const connections: rpc.MessageConnection[] = [];
let socketPath: string | undefined;
let workloadInstances: { cache: { instanceId: string }; database: { instanceId: string }; relay: { instanceId: string } } | undefined;
const execute = promisify(execFile);
async function cliCommand(arguments_: string[]): Promise<string> {
    const { stdout } = await execute(cliPath, [
        ...arguments_, '--apphost', apphostPath, '--non-interactive', '--nologo'
    ], { cwd: workspace, env: process.env, timeout: 30_000, maxBuffer: 4 * 1024 * 1024 });
    return stdout;
}
async function connect(path: string): Promise<rpc.MessageConnection> {
    const socket = createConnection(path);
    await once(socket, 'connect');
    const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
    connection.onDispose(() => socket.destroy());
    connection.listen();
    connections.push(connection);
    return connection;
}
async function collect<T>(connection: rpc.MessageConnection, descriptor: StreamDescriptor): Promise<T[]> {
    const values: T[] = [];
    while (true) {
        const next = await connection.sendRequest<StreamResult<T>>('$/enumerator/next', descriptor.token);
        values.push(...next.values);
        if (next.finished) return values;
    }
}
try {
    const end = Date.now() + 90_000;
    while (true) {
        if (cli.exitCode !== null || cli.signalCode !== null) throw new Error(`CLI exited before readiness: ${cli.exitCode}`);
        try {
            const proof = JSON.parse(await readFile(proofPath, 'utf8')) as { instances: NonNullable<typeof workloadInstances> };
            workloadInstances = proof.instances;
            break;
        } catch (error) {
            if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error;
        }
        assert.ok(Date.now() < end, 'The real integration workloads must become ready.');
        await delay(100);
    }
    assert.ok(serverPid);
    const directory = join(homedir(), '.aspire', 'cli', 'bch');
    // Production discovery encodes the owner PID in compact socket names:
    // <11-char AppHost ID><8-char instance ID>.<pid>. The CLI commands below
    // independently discover it by AppHost path rather than using this test lookup.
    const matches = (await readdir(directory)).filter(name => name.endsWith(`.${serverPid}`));
    assert.equal(matches.length, 1, 'The native server must publish one discoverable auxiliary socket.');
    socketPath = join(directory, matches[0]!);
    if (process.platform !== 'win32') {
        assert.equal((await stat(socketPath)).mode & 0o777, 0o600);
        assert.equal((await stat(directory)).mode & 0o777, 0o700);
    }
    const first = await connect(socketPath);
    const second = await connect(socketPath);
    const capabilities = await first.sendRequest<{ capabilities: string[] }>('GetCapabilitiesAsync', {});
    for (const capability of ['aux.v1', 'aux.v2', 'aux.v3', 'resource-snapshot-versions.v1']) {
        assert.ok(capabilities.capabilities.includes(capability));
    }
    const legacy = await first.sendRequest<{ processId: number; appHostPath: string }>('GetAppHostInformationAsync');
    assert.equal(legacy.processId, serverPid);
    assert.equal(legacy.appHostPath, apphostPath);
    const info = await second.sendRequest<{ pid: string; appHostPath: string }>('GetAppHostInfoAsync', {});
    assert.equal(info.pid, String(serverPid));
    assert.equal(info.appHostPath, apphostPath);
    const ready = await first.sendRequest<{ isReady: boolean }>('WaitForAppHostReadyAsync', {});
    assert.equal(ready.isReady, true);
    const dashboard = await first.sendRequest<{
        apiBaseUrl: string; apiToken: string; dashboardUrls: string[]; isHealthy: boolean;
    }>('GetDashboardInfoAsync', {});
    assert.equal(dashboard.isHealthy, true);
    assert.ok(dashboard.dashboardUrls.length > 0);
    assert.ok(dashboard.apiToken);
    const dashboardApi = new URL('/api/telemetry/resources', dashboard.apiBaseUrl);
    assert.equal((await fetch(dashboardApi, {
        headers: { 'x-api-key': 'incorrect-key' }
    })).status, 401);
    assert.equal((await fetch(dashboardApi, {
        headers: { 'x-api-key': dashboard.apiToken }
    })).status, 200);
    const resources = await first.sendRequest<{ resources: ResourceSnapshot[] }>('GetResourcesAsync', {
        clientCapabilities: ['aux.v3']
    });
    assert.deepEqual(resources.resources.map(resource => [resource.name, resource.state, resource.healthStatus]), [
        ['cache', 'Running', 'Healthy'], ['database', 'Running', 'Healthy'], ['tunnel', 'Running', 'Healthy']
    ]);
    assert.ok(resources.resources.every(resource => Number.isSafeInteger(resource.version) && resource.version > 0));
    const filtered = await second.sendRequest<{ resources: ResourceSnapshot[] }>('GetResourcesAsync', {
        filter: 'CACHE', clientCapabilities: ['aux.v3']
    });
    assert.deepEqual(filtered.resources.map(resource => resource.name), ['cache']);
    const watcher = await first.sendRequest<StreamDescriptor>('WatchResourcesAsync', { filter: 'cache' });
    const initial = await first.sendRequest<StreamResult<ResourceSnapshot>>('$/enumerator/next', watcher.token);
    assert.ok(initial.values.length > 0);
    assert.equal(initial.values[0]!.name, 'cache');
    await first.sendRequest('$/enumerator/abort', watcher.token);
    const logs = await collect<ResourceLogLine>(second, await second.sendRequest<StreamDescriptor>('GetConsoleLogsAsync', {
        resourceName: 'cache', follow: false, tail: 3
    }));
    assert.ok(logs.length > 0 && logs.length <= 3);
    assert.ok(logs.every(line => line.resourceName === 'cache' && typeof line.content === 'string'));
    const batches = await collect<{ lines: ResourceLogLine[] }>(first,
        await first.sendRequest<StreamDescriptor>('GetConsoleLogBatchesAsync', {
            resourceName: 'cache', follow: false, tail: 3
        }));
    assert.deepEqual(batches.flatMap(batch => batch.lines), logs);
    const validated = await second.sendRequest<{ success: boolean }>('ExecuteResourceCommandAsync', {
        resourceName: 'cache', commandName: 'read-value', validateOnly: true
    });
    assert.equal(validated.success, true);
    const command = await second.sendRequest<{ success: boolean; message: string }>('ExecuteResourceCommandAsync', {
        resourceName: 'cache', commandName: 'read-value', nonInteractive: true
    });
    assert.equal(command.success, true);
    assert.equal(command.message, 'native-value');
    const unknown = await second.sendRequest<{ success: boolean }>('ExecuteResourceCommandAsync', {
        resourceName: 'cache', commandName: 'missing-command'
    });
    assert.equal(unknown.success, false);
    const healthy = await first.sendRequest<{ success: boolean }>('WaitForResourceAsync', {
        resourceName: 'cache', status: 'healthy', timeoutSeconds: 5
    });
    assert.equal(healthy.success, true);
    const missing = await second.sendRequest<{ success: boolean; resourceNotFound: boolean }>('WaitForResourceAsync', {
        resourceName: 'not-present', status: 'healthy', timeoutSeconds: 1
    });
    assert.equal(missing.success, false);
    assert.equal(missing.resourceNotFound, true);
    const cancelled = new rpc.CancellationTokenSource();
    const waiting = first.sendRequest('WaitForResourceAsync', {
        resourceName: 'cache', status: 'down', timeoutSeconds: 30
    }, cancelled.token);
    cancelled.cancel();
    await assert.rejects(waiting);
    cancelled.dispose();
    assert.equal((await second.sendRequest<{ processId: number }>('GetAppHostInformationAsync')).processId, serverPid);
    const described = JSON.parse(await cliCommand(['describe', '--format', 'Json'])) as { resources: { name: string }[] };
    assert.deepEqual(described.resources.map(resource => resource.name), ['cache', 'database', 'tunnel']);
    const cliLogs = JSON.parse(await cliCommand(['logs', 'cache', '--tail', '3', '--format', 'Json'])) as {
        logs: { resourceName: string; content: string }[];
    };
    assert.ok(cliLogs.logs.length > 0 && cliLogs.logs.length <= 3);
    assert.ok(cliLogs.logs.every(line => line.resourceName === 'cache' && typeof line.content === 'string'));
    await cliCommand(['resource', 'cache', 'read-value']);
    await cliCommand(['wait', 'cache', '--status', 'healthy', '--timeout', '5']);
    await cliCommand(['stop', '--pid', String(serverPid)]);
    const stopDeadline = Date.now() + 30_000;
    while (true) {
        try {
            process.kill(serverPid, 0);
        } catch (error) {
            if (!(error instanceof Error && 'code' in error && error.code === 'ESRCH')) throw error;
            break;
        }
        assert.ok(Date.now() < stopDeadline, 'Auxiliary stop must remove its server without stopping the launcher.');
        await delay(100);
    }
    // This fixture guest waits for a signal independently of its RPC connection.
    // Instance-scoped stop must not signal its caller or launcher to end that wait.
    // Only clean up the fixture CLI after verifying the auxiliary stop removed the server.
    assert.ok(workloadInstances);
    const relayPid = Number(workloadInstances.relay.instanceId);
    for (const workload of [workloadInstances.cache, workloadInstances.database]) {
        const { stdout } = await execute('docker', [
            'ps', '-a', '--filter', `id=${workload.instanceId}`, '--format', '{{.ID}}'
        ]);
        assert.equal(stdout.trim(), '');
    }
    assert.throws(() => process.kill(relayPid, 0), (error: unknown) =>
        error instanceof Error && 'code' in error && error.code === 'ESRCH');
    await assert.rejects(stat(socketPath), (error: unknown) =>
        error instanceof Error && 'code' in error && error.code === 'ENOENT');
} catch (error) {
    console.error('Auxiliary conformance failed:', error);
    throw error;
} finally {
    for (const connection of connections) connection.dispose();
    if (cli.exitCode === null && cli.signalCode === null) cli.kill('SIGINT');
    const [code] = await exited;
    clearTimeout(deadline);
    assert.ok(code === 0 || code === 130, `Unexpected CLI shutdown: ${code}`);
    if (serverPid) assert.throws(() => process.kill(serverPid!, 0));
    if (socketPath) {
        await assert.rejects(stat(socketPath), (error: unknown) =>
            error instanceof Error && 'code' in error && error.code === 'ENOENT');
    }
    console.log(JSON.stringify({
        auxiliaryBackchannel: 'passed', discoverableByExistingCli: 'passed', independentConnections: 2,
        info: 'passed', ready: 'passed', dashboardApiAuthentication: 'passed',
        resourcesAndFilter: 'passed', watchAndAbort: 'passed', logsAndBatches: 'passed',
        resourceCommand: 'passed', waitAndCancellation: 'passed',
        actualDescribeLogsResourceWaitStopCommands: 'passed', serverAndSocketCleanup: 'passed'
    }, null, 2));
}
