// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { execFileSync, fork, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { connectNativeAppHost } from './native-client.mjs';
import type { AspireClient } from './transport.mjs';
import type { StartedWorkload } from './aspire.mjs';
import { NativeDashboardSmoke } from './NativeDashboardSmoke.mjs';

interface Ready {
    type: 'ready';
    cache: StartedWorkload;
    database: StartedWorkload;
    relay: StartedWorkload;
    postgresQueryValue: string;
}
const binary = process.argv[2];
assert.ok(binary && process.env.ASPIRE_NATIVE_DCP_PATH, 'Pass the native server and set ASPIRE_NATIVE_DCP_PATH.');
const token = randomBytes(32).toString('hex');
const dashboardKey = randomBytes(32).toString('hex');
const server = spawn(binary, ['--port', '0'], {
    env: {
        ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token,
        ...(process.env.ASPIRE_NATIVE_DASHBOARD_DLL ? {
            ASPIRE_NATIVE_DASHBOARD_PORT: '0', ASPIRE_NATIVE_DASHBOARD_API_KEY: dashboardKey
        } : {})
    },
    stdio: ['ignore', 'pipe', 'inherit']
});
const timeout = setTimeout(() => server.kill('SIGKILL'), 180_000);
let author: AspireClient | undefined;
let observerClient: AspireClient | undefined;
let worker: ReturnType<typeof fork> | undefined;
let dashboardUi: NativeDashboardSmoke | undefined;
async function stopWorker(child: ReturnType<typeof fork>): Promise<number | null> {
    if (child.exitCode !== null || child.signalCode !== null) return child.exitCode;
    const exited = once(child, 'exit');
    if (child.connected) {
        await new Promise<void>((resolve, reject) => child.send('stop', error => {
            if (error && (!('code' in error) || !['EPIPE', 'ERR_IPC_CHANNEL_CLOSED'].includes(String(error.code)))) reject(error);
            else resolve();
        }));
    }
    const deadline = setTimeout(() => child.kill('SIGKILL'), 10000);
    try {
        const [code] = await exited;
        return code;
    } finally {
        clearTimeout(deadline);
    }
}
try {
    const lines = createInterface({ input: server.stdout });
    let resourceService: string | undefined;
    const line = await Promise.race([
        (async () => {
            for await (const line of lines) {
                const dashboard = /^Native Dashboard resource service listening on (http:\/\/127\.0\.0\.1:\d+\/)\.$/.exec(line);
                if (dashboard) resourceService = dashboard[1];
                if (line.startsWith('Native AppHost server listening on ')) return line;
            }
            throw new Error('Native server did not publish its socket address.');
        })(),
        once(server, 'exit').then(([code]) => { throw new Error(`Native server exited during startup (${code}).`); })
    ]);
    lines.close();
    server.stdout.pipe(process.stderr);
    const match = /^Native AppHost server listening on 127\.0\.0\.1:(\d+)\.$/.exec(line);
    assert.ok(match);
    const options = { endpoint: { host: '127.0.0.1' as const, port: Number(match[1]) }, authenticationToken: token };
    if (process.env.ASPIRE_NATIVE_DASHBOARD_DLL) {
        assert.ok(resourceService);
        dashboardUi = await NativeDashboardSmoke.start(process.env.ASPIRE_NATIVE_DASHBOARD_DLL, resourceService, dashboardKey);
    }
    const apphost = await connectNativeAppHost(options);
    const dashboard = await connectNativeAppHost(options);
    author = apphost.client;
    observerClient = dashboard.client;
    const session = await apphost.server.openApplication();
    const composition = await session.startGeneration();
    const cache = await composition.addResource('cache', 'native.testing/Redis');
    const database = await composition.addResource('database', 'native.testing/Postgres');
    const tunnel = await composition.addResource('tunnel', 'native.testing/LocalTunnel');
    await tunnel.waitFor(cache);
    const execution = await composition.createExecution();
    const observer = await dashboard.server.joinApplicationObserver(await execution.inviteApplicationObserver());
    worker = fork(fileURLToPath(new URL('./NativeIntegrationWorker.mjs', import.meta.url)), [], {
        stdio: ['ignore', 'inherit', 'inherit', 'ipc']
    });
    const readyMessage = Promise.race([
        once(worker, 'message'),
        once(worker, 'exit').then(([code]) => { throw new Error(`Integration host exited before readiness (${code}).`); })
    ]);
    worker.send({
        port: options.endpoint.port, token, password: randomBytes(24).toString('hex'),
        redisInvitation: await execution.inviteResourceExecution(cache),
        postgresInvitation: await execution.inviteResourceExecution(database),
        tunnelInvitation: await execution.inviteResourceExecution(tunnel)
    });
    let ready = false;
    const initialized = readyMessage.then(async ([message]) => {
        if (message && typeof message === 'object' && 'type' in message && message.type === 'failed') {
            const snapshot = await observer.readResourceObservations();
            for (const resource of snapshot.resources ?? []) {
                assert.ok(resource.resourceId);
                const logs = await observer.readResourceLogs(resource.resourceId, 0);
                for (const entry of logs.entries ?? []) console.error(`[${resource.name}] ${entry.stream}: ${entry.message}`);
            }
        }
        assert.ok(message && typeof message === 'object' && 'type' in message && message.type === 'ready',
            `Integration host failed: ${JSON.stringify(message)}`);
        ready = true;
        return message as Ready;
    });
    const interactions = (async () => {
        if (dashboardUi) {
            await dashboardUi.confirm();
            return;
        }
        while (!ready) {
            const confirmations = await observer.readConfirmations();
            for (const request of confirmations.requests ?? []) {
                assert.ok(request.requestId && request.message === 'Keep the initialized database running?');
                await observer.respondConfirmation(request.requestId, true);
            }
            const snapshot = await observer.readResourceObservations();
            assert.ok(snapshot.version !== undefined);
            await observer.waitResourceObservations(snapshot.version, 1000);
        }
    })();
    const [workloads] = await Promise.all([initialized, interactions]);
    assert.equal(workloads.postgresQueryValue, 'native-value');
    const observed = await observer.readResourceObservations();
    assert.ok(observed.resources);
    assert.deepEqual(observed.resources.map(resource => [resource.name, resource.state, resource.healthy]), [
        ['cache', 'Running', true], ['database', 'Running', true], ['tunnel', 'Running', true]
    ]);
    await dashboardUi?.verifyResources();
    const command = await observer.invokeResourceCommand('cache', 'read-value');
    const result = await command.awaitRuntimeOperation();
    assert.deepEqual(result, { status: 'succeeded', message: 'native-value' });
    await author.flushPendingPromises();
    await observerClient.flushPendingPromises();
    assert.equal(await stopWorker(worker), 0);
    worker = undefined;
    await session.retireGeneration(composition);
    for (const endpoint of [workloads.cache, workloads.database]) {
        assert.ok(endpoint.instanceId);
        assert.equal(execFileSync('docker', ['ps', '-a', '--filter', `id=${endpoint.instanceId}`, '--format', '{{.ID}}'],
            { encoding: 'utf8' }).trim(), '', 'Retirement must remove the actual container, not only its DCP object.');
    }
    assert.ok(workloads.relay.instanceId);
    assert.throws(() => process.kill(Number(workloads.relay.instanceId), 0), (error: unknown) =>
        error instanceof Error && 'code' in error && error.code === 'ESRCH');
    const replacement = await session.startGeneration();
    await replacement.addResource('cache', 'native.testing/Redis');
    assert.equal((await replacement.inspect()).resources?.length, 1);
    console.log(JSON.stringify({
        nativeWorkloadRoundTrips: 'passed',
        separateIntegrationProcess: 'passed',
        redisThroughAllocatedEndpoint: 'passed',
        postgresThroughAllocatedEndpoint: 'passed',
        localTunnelForwarding: 'passed',
        command: 'passed',
        confirmation: 'passed',
        ownedContainersAndExecutableRemoved: 'passed',
        generationReplacement: 'passed',
        actualDashboardResourcesAndConfirmation: dashboardUi ? 'passed' : 'not exercised',
        liveDevTunnelsService: 'not exercised'
    }, null, 2));
} finally {
    try {
        if (worker) await stopWorker(worker);
    } finally {
        await dashboardUi?.dispose();
        author?.disconnect();
        observerClient?.disconnect();
        const exited = server.exitCode === null && server.signalCode === null
            ? once(server, 'exit') : Promise.resolve([server.exitCode]);
        if (server.exitCode === null && server.signalCode === null) server.kill('SIGINT');
        const [code] = await exited;
        clearTimeout(timeout);
        assert.equal(code, 0);
    }
}
