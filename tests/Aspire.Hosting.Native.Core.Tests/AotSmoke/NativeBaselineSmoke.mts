// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { existsSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createConnection } from 'node:net';
import { homedir, tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import * as rpc from 'vscode-jsonrpc/node.js';
import { connectNativeAppHost } from './native-client.mjs';
import type { AspireClient } from './transport.mjs';
import type { ApplicationObserver, ContainerWorkloadLaunch, WorkloadAllocation } from './aspire.mjs';

const binary = process.argv[2];
assert.ok(binary && process.env.ASPIRE_NATIVE_DCP_PATH, 'Pass the published server and set ASPIRE_NATIVE_DCP_PATH.');
const runs = 3;
const results: unknown[] = [];

async function response(url: string, expected: string): Promise<void> {
    const deadline = Date.now() + 30_000;
    while (true) {
        try {
            const result = await fetch(url, { signal: AbortSignal.timeout(2000) });
            assert.equal(result.status, 200);
            assert.equal(await result.text(), expected);
            return;
        } catch (error) {
            // Node fetch reports startup refusal/reset through error.cause.code.
            // Assertion failures and application HTTP errors are not readiness retries.
            const code = error instanceof Error && error.cause && typeof error.cause === 'object' && 'code' in error.cause
                ? String(error.cause.code) : undefined;
            if (!['ECONNREFUSED', 'ECONNRESET', 'UND_ERR_SOCKET'].includes(code ?? '') || Date.now() >= deadline) throw error;
            await delay(100);
        }
    }
}

function allocationUrls(allocation: WorkloadAllocation): string[] {
    assert.ok(allocation.endpoints);
    return allocation.endpoints.map(endpoint => {
        assert.ok(endpoint.host && endpoint.port && endpoint.uriScheme);
        return `${endpoint.uriScheme}://${endpoint.host}:${endpoint.port}`;
    });
}

function processSamples(serverPid: number): { pid: number; role: string; rssBytes: number }[] {
    // ps emits "<pid> <ppid> <rss KiB> <command...>". Parse the three numeric
    // columns only; spaces in executable paths/arguments are retained as command text.
    const processes = execFileSync('ps', ['-axo', 'pid=,ppid=,rss=,command='], { encoding: 'utf8' })
        .split('\n').map(line => /^\s*(\d+)\s+(\d+)\s+(\d+)\s+(.+)$/.exec(line))
        .filter(match => match !== null).map(match => ({
            pid: Number(match[1]), parent: Number(match[2]), rssBytes: Number(match[3]) * 1024, command: match[4]
        }));
    const owned = new Set([serverPid]);
    let changed = true;
    while (changed) {
        changed = false;
        for (const process of processes) {
            if (owned.has(process.parent) && !owned.has(process.pid)) {
                owned.add(process.pid);
                changed = true;
            }
        }
    }
    return processes.filter(process => owned.has(process.pid)).map(process => ({
        pid: process.pid, rssBytes: process.rssBytes,
        role: process.pid === serverPid ? 'server' : /\bdcp\b/.test(process.command) ? 'dcp' : 'workload-or-helper'
    }));
}

async function run(controller: boolean, index: number): Promise<void> {
    const directory = mkdtempSync(join(tmpdir(), 'native-baseline-'));
    const token = randomBytes(32).toString('hex');
    const environment: NodeJS.ProcessEnv = { ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token,
        ASPIRE_NATIVE_APPHOST_PATH: fileURLToPath(import.meta.url) };
    for (const key of ['ASPIRE_DASHBOARD_PATH', 'ASPIRE_NATIVE_DASHBOARD_PORT', 'ASPIRE_BACKCHANNEL_PATH',
        'AppHost__FilePath', 'AppHost__Path', 'ASPIRE_APPHOST_FILEPATH', 'REMOTE_APP_HOST_SOCKET_PATH']) {
        delete environment[key];
    }
    if (!controller) delete environment.ASPIRE_NATIVE_DCP_PATH;
    else environment.ASPIRE_NATIVE_DASHBOARD_PORT = '0';
    const started = performance.now();
    const child = spawn(binary, ['--port', '0'], { env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
    assert.ok(child.pid);
    const exited = once(child, 'exit');
    const emergency = setTimeout(() => child.kill('SIGKILL'), 180_000);
    const clients: AspireClient[] = [];
    let auxiliary: rpc.MessageConnection | undefined;
    let auxiliarySocket: ReturnType<typeof createConnection> | undefined;
    const containerIds: string[] = [];
    const workloadPids: number[] = [];
    const volume = `native-baseline-${randomBytes(8).toString('hex')}`;
    let volumeCreated = false;
    let observer: ApplicationObserver | undefined;
    const stderr: Buffer[] = [];
    child.stderr.on('data', bytes => stderr.push(Buffer.from(bytes)));
    try {
        const lines = createInterface({ input: child.stdout });
        let address: string | undefined;
        for await (const line of lines) {
            // Startup publishes a bound address, not application readiness:
            // "Native AppHost server listening on 127.0.0.1:<port>."
            const match = /^Native AppHost server listening on 127\.0\.0\.1:(\d+)\.$/.exec(line);
            if (match) { address = match[1]; break; }
        }
        lines.close();
        child.stdout.resume();
        assert.ok(address, 'The server exited before binding its transport.');
        const boundMilliseconds = performance.now() - started;
        const connectionOptions = {
            endpoint: { host: '127.0.0.1' as const, port: Number(address) },
            authenticationToken: token, authenticationTimeoutMilliseconds: 10_000
        };
        const author = await connectNativeAppHost(connectionOptions);
        clients.push(author.client);
        const workspace = await author.server.createApplicationWorkspace();
        let revision = await workspace.beginRevision();
        await workspace.commitRevision(revision);
        const sockets = readdirSync(join(homedir(), '.aspire', 'cli', 'bch'))
            .filter(name => name.endsWith(`.${child.pid}`));
        assert.equal(sockets.length, 1);
        const socketPath = join(homedir(), '.aspire', 'cli', 'bch', sockets[0]);
        auxiliarySocket = createConnection(socketPath);
        await once(auxiliarySocket, 'connect');
        auxiliary = rpc.createMessageConnection(new rpc.StreamMessageReader(auxiliarySocket), new rpc.StreamMessageWriter(auxiliarySocket));
        auxiliary.listen();
        const readiness = await auxiliary.sendRequest<{ isReady: boolean }>('WaitForAppHostReadyAsync', {});
        assert.equal(readiness.isReady, true);
        const readyMilliseconds = performance.now() - started;
        await delay(250);
        const idle = processSamples(child.pid);
        revision = await workspace.beginRevision();
        for (let resource = 0; resource < 100; resource++) {
            await revision.addResource(`resource-${resource}`, 'baseline/Resource');
        }
        await workspace.commitRevision(revision);
        const declarations = await revision.inspect();
        assert.equal(declarations.resources?.length, 100);
        const composed = processSamples(child.pid);
        let running: ReturnType<typeof processSamples> | undefined;
        let workloadReadyMilliseconds: number | undefined;
        if (controller) {
            revision = await workspace.beginRevision();
            const service = await revision.addResource('service', 'baseline/Container');
            const executable = await revision.addResource('process', 'baseline/Executable');
            await workspace.commitRevision(revision);
            const execution = await workspace.getApplicationExecution();
            observer = await author.server.joinApplicationObserver(await execution.inviteApplicationObserver());
            const provider = await connectNativeAppHost(connectionOptions);
            clients.push(provider.client);
            const serviceOwner = await provider.server.joinResourceExecution(await execution.inviteResourceExecution(service));
            const executableOwner = await provider.server.joinResourceExecution(await execution.inviteResourceExecution(executable));
            writeFileSync(join(directory, 'index.html'), 'retained baseline data');
            writeFileSync(join(directory, 'nginx.conf'),
                'worker_processes 1; events {} http { server { listen 8080; root /state; } server { listen 8081; root /state; } }');
            execFileSync('docker', ['volume', 'create', volume], { stdio: 'pipe' });
            volumeCreated = true;
            const launch: ContainerWorkloadLaunch = {
                image: 'nginx:1.29-alpine', entrypoint: '/bin/sh',
                arguments: ['-c', 'test -f /state/index.html || cp /input/index.html /state/index.html; exec nginx -g "daemon off;" -c /input/nginx.conf'],
                environment: [], mounts: [
                    { type: 'bind', source: directory, target: '/input', readOnly: true },
                    { type: 'volume', source: volume, target: '/state', readOnly: false }
                ], endpoints: [
                    { name: 'http', protocol: 'TCP', uriScheme: 'http', targetPort: 8080, hostPort: 0, environmentVariable: '' },
                    { name: 'management', protocol: 'TCP', uriScheme: 'http', targetPort: 8081, hostPort: 0, environmentVariable: '' }
                ]
            };
            const first = await serviceOwner.startContainerWorkload(launch);
            assert.ok(first.instanceId);
            containerIds.push(first.instanceId);
            assert.deepEqual(first.endpoints?.map(endpoint => endpoint.name), ['http', 'management']);
            await Promise.all(allocationUrls(first).map(url => response(url, 'retained baseline data')));
            const program = "import {createServer} from 'node:http'; for (const key of ['HTTP_PORT','METRICS_PORT']) createServer((_,res)=>res.end(key)).listen(Number(process.env[key]),'127.0.0.1');";
            const processAllocation = await executableOwner.startExecutableWorkload({
                executablePath: process.execPath, workingDirectory: directory,
                environment: [], arguments: ['--input-type=module', '-e', program], endpoints: [
                    { name: 'http', protocol: 'TCP', uriScheme: 'http', targetPort: 0, hostPort: 0, environmentVariable: 'HTTP_PORT' },
                    { name: 'metrics', protocol: 'TCP', uriScheme: 'http', targetPort: 0, hostPort: 0, environmentVariable: 'METRICS_PORT' }
                ]
            });
            assert.ok(processAllocation.instanceId);
            workloadPids.push(Number(processAllocation.instanceId));
            const urls = allocationUrls(processAllocation);
            await Promise.all([response(urls[0], 'HTTP_PORT'), response(urls[1], 'METRICS_PORT')]);
            await serviceOwner.publishObservation({ state: 'Running', healthy: true, urls: allocationUrls(first) });
            await executableOwner.publishObservation({ state: 'Running', healthy: true, urls });
            workloadReadyMilliseconds = performance.now() - started;
            running = processSamples(child.pid);
            revision = await workspace.beginRevision();
            const next = await revision.addResource('service', 'baseline/Container');
            await revision.addResource('process', 'baseline/Executable');
            await next.setResourceConfiguration({ properties: [{ name: 'restart', value: 'requested' }] });
            await workspace.commitRevision(revision);
            const configuration = await serviceOwner.readResourceConfiguration();
            assert.ok(configuration.revision);
            const second = await serviceOwner.restartContainerWorkload(configuration.revision, launch);
            assert.ok(second.instanceId && second.instanceId !== first.instanceId);
            containerIds.push(second.instanceId);
            assert.deepEqual(second.endpoints?.map(endpoint => endpoint.name), ['http', 'management']);
            await Promise.all(allocationUrls(second).map(url => response(url, 'retained baseline data')));
            await serviceOwner.completeResourceConfiguration(configuration.revision, { status: 'succeeded', message: '' });
        }
        await workspace.retireApplicationWorkspace();
        child.kill('SIGTERM');
        const [code, signal] = await exited;
        assert.equal(code, 0, `Server exit ${code}/${signal}:\n${Buffer.concat(stderr).toString()}`);
        assert.equal(existsSync(socketPath), false, 'Owned auxiliary socket survived shutdown.');
        for (const container of containerIds) {
            const inspection = spawn('docker', ['inspect', container], { stdio: 'ignore' });
            const [status] = await once(inspection, 'exit');
            assert.equal(status, 1, 'Owned container survived retirement.');
        }
        for (const pid of workloadPids) {
            assert.throws(() => process.kill(pid, 0), /ESRCH/, 'Owned executable survived retirement.');
        }
        results.push({ mode: controller ? 'dcp-and-resource-service' : 'contract-only', run: index,
            boundMilliseconds, readyMilliseconds, workloadReadyMilliseconds, idle, composed, running,
            cleanup: 'passed' });
    } catch (error) {
        process.stderr.write(Buffer.concat(stderr));
        if (observer) {
            try {
                const observations = await observer.readResourceObservations();
                for (const resource of observations.resources ?? []) {
                    assert.ok(resource.resourceId);
                    process.stderr.write(JSON.stringify(await observer.readResourceLogs(resource.resourceId, 0)) + '\n');
                }
            } catch (diagnosticError) {
                process.stderr.write(`Baseline resource diagnostics failed: ${String(diagnosticError)}\n`);
            }
        }
        throw error;
    } finally {
        if (child.exitCode === null && child.signalCode === null) {
            child.kill('SIGTERM');
            await exited;
        }
        auxiliary?.dispose();
        auxiliarySocket?.destroy();
        for (const client of clients) client.disconnect();
        if (volumeCreated) {
            // The caller owns this explicitly named volume. Server retirement
            // removes workloads, not data retained independently of them.
            execFileSync('docker', ['volume', 'rm', volume], { stdio: 'pipe' });
        }
        clearTimeout(emergency);
        rmSync(directory, { recursive: true, force: true });
    }
}

for (let runIndex = 0; runIndex < runs; runIndex++) {
    await run(false, runIndex);
    await run(true, runIndex);
}
console.log(JSON.stringify({
    executable: basename(binary), executableBytes: statSync(binary).size,
    accounting: 'RSS is per-process resident memory, not unique physical memory. Server children are reported separately. Docker VM/containers and the driving Node process are excluded. Startup begins at direct server launch, not aspire run.',
    results
}, null, 2));
