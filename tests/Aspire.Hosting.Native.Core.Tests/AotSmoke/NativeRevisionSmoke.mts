// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { execFileSync, fork, spawn, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { connectNativeAppHost } from './native-client.mjs';
import type { ApplicationObserver, StartedWorkload } from './aspire.mjs';
import type { AspireClient } from './transport.mjs';
import { NativeDashboardSmoke } from './NativeDashboardSmoke.mjs';
import { redisCommand } from './NativeRedisClient.mjs';

interface Committed {
    type: 'committed';
    workspaceInvitation: string;
    observerInvitation?: string;
    redisInvitation?: string;
    postgresInvitation?: string;
    tunnelInvitation?: string;
}
interface Ready {
    type: 'ready';
    cache: StartedWorkload;
    database: StartedWorkload;
    relay: StartedWorkload;
}
const binary = process.argv[2];
assert.ok(binary && process.env.ASPIRE_NATIVE_DCP_PATH);
const token = randomBytes(32).toString('hex');
const password = randomBytes(24).toString('hex');
const dashboardKey = randomBytes(32).toString('hex');
const server = spawn(binary, ['--port', '0'], {
    env: {
        ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token,
        ...(process.env.ASPIRE_NATIVE_DASHBOARD_DLL ? {
            ASPIRE_NATIVE_DASHBOARD_PORT: '0', ASPIRE_NATIVE_DASHBOARD_API_KEY: dashboardKey
        } : {})
    }, stdio: ['ignore', 'pipe', 'inherit']
});
const deadline = setTimeout(() => server.kill('SIGKILL'), 180_000);
const authors = new Set<ReturnType<typeof fork>>();
let worker: ReturnType<typeof fork> | undefined;
let observerClient: AspireClient | undefined;
let dashboard: NativeDashboardSmoke | undefined;
let port = 0;
let executions = 0;
let workspaceInvitation: string | undefined;
const updates = new Map<string, StartedWorkload>();
let integrationFailure: unknown;

type AppHostResult = Committed | { type: 'retired' } | { type: 'discarded'; workspaceInvitation: string };

async function executeAppHost(model: Record<string, unknown>): Promise<AppHostResult> {
    const child = fork(fileURLToPath(new URL('./NativeRevisionAppHost.mjs', import.meta.url)), [], {
        stdio: ['ignore', 'inherit', 'inherit', 'ipc']
    });
    authors.add(child);
    const exited = once(child, 'exit');
    const childDeadline = setTimeout(() => child.kill('SIGKILL'), 45_000);
    try {
        const response = Promise.race([
            once(child, 'message').then(([message]) => message),
            exited.then(([code]) => { throw new Error(`AppHost exited without committing (${code}).`); })
        ]);
        child.send({ port, token, workspaceInvitation, ...model });
        const message = await response as AppHostResult;
        assert.ok(message && ['committed', 'retired', 'discarded'].includes(message.type));
        const [code] = await exited;
        assert.equal(code, 0);
        executions++;
        if (message.type !== 'retired') workspaceInvitation = message.workspaceInvitation;
        return message;
    } finally {
        clearTimeout(childDeadline);
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
        authors.delete(child);
    }
}

async function waitHealthy(observer: ApplicationObserver, expectedNames: string[]): Promise<void> {
    const end = Date.now() + 45_000;
    while (true) {
        if (integrationFailure) throw integrationFailure;
        const snapshot = await observer.readResourceObservations();
        assert.ok(snapshot.version !== undefined && snapshot.resources);
        if (JSON.stringify(snapshot.resources.map(resource => resource.name)) === JSON.stringify(expectedNames) &&
            snapshot.resources.every(resource => resource.healthy && resource.configurationStatus === 'succeeded')) return;
        if (Date.now() > end) throw new Error(`Revision did not become healthy: ${JSON.stringify(snapshot)}`);
        await observer.waitResourceObservations(snapshot.version, 1000);
    }
}

async function readValue(observer: ApplicationObserver): Promise<string | undefined> {
    const command = await observer.invokeResourceCommand('cache', 'read-value');
    const result = await command.awaitRuntimeOperation();
    assert.equal(result.status, 'succeeded');
    return result.message;
}

async function expectRetirement(resources: string[]): Promise<void> {
    assert.ok(worker?.connected);
    const expected = waitForMessage(worker, 'retirementExpected', 10_000);
    worker.send({ type: 'expectRetirement', resources });
    await expected;
}

async function waitForMessage(child: ChildProcess, type: string, timeoutMilliseconds: number): Promise<unknown> {
    const cancellation = new AbortController();
    const timeout = setTimeout(() => cancellation.abort(), timeoutMilliseconds);
    const signal = cancellation.signal;
    try {
        while (true) {
            const [message] = await Promise.race([
                once(child, 'message', { signal }),
                once(child, 'exit', { signal }).then(([code]) => {
                    throw new Error(`Integration exited while waiting for ${type} (${code}).`);
                })
            ]);
            if (message && typeof message === 'object' && 'type' in message) {
                if (message.type === 'failed') throw new Error(`Integration failed: ${JSON.stringify(message)}`);
                if (message.type === type) return message;
            }
        }
    } finally {
        clearTimeout(timeout);
        cancellation.abort();
    }
}

function containerExists(endpoint: StartedWorkload): boolean {
    assert.ok(endpoint.instanceId);
    return execFileSync('docker', ['ps', '-a', '--filter', `id=${endpoint.instanceId}`, '--format', '{{.ID}}'],
        { encoding: 'utf8' }).trim() !== '';
}

try {
    let resourceService: string | undefined;
    const lines = createInterface({ input: server.stdout });
    for await (const line of lines) {
        const dashboardAddress = /^Native Dashboard resource service listening on (http:\/\/127\.0\.0\.1:\d+\/)\.$/.exec(line);
        if (dashboardAddress) resourceService = dashboardAddress[1];
        const address = /^Native AppHost server listening on 127\.0\.0\.1:(\d+)\.$/.exec(line);
        if (address) { port = Number(address[1]); break; }
    }
    lines.close();
    server.stdout.pipe(process.stderr);
    assert.ok(port > 0);
    const first = await executeAppHost({});
    assert.equal(first.type, 'committed');
    const invitations = first as Committed;
    assert.ok(invitations.observerInvitation && invitations.redisInvitation && invitations.postgresInvitation && invitations.tunnelInvitation);
    const connection = await connectNativeAppHost({
        endpoint: { host: '127.0.0.1', port }, authenticationToken: token, authenticationTimeoutMilliseconds: 10_000
    });
    observerClient = connection.client;
    const observer = await connection.server.joinApplicationObserver(invitations.observerInvitation);
    if (process.env.ASPIRE_NATIVE_DASHBOARD_DLL) {
        assert.ok(resourceService);
        dashboard = await NativeDashboardSmoke.start(process.env.ASPIRE_NATIVE_DASHBOARD_DLL, resourceService, dashboardKey);
    }
    worker = fork(fileURLToPath(new URL('./NativeIntegrationWorker.mjs', import.meta.url)), [], {
        stdio: ['ignore', 'inherit', 'inherit', 'ipc']
    });
    worker.on('message', message => {
        if (message && typeof message === 'object' && 'type' in message) {
            if (message.type === 'configurationApplied' && 'resource' in message && 'endpoint' in message)
                updates.set(String(message.resource), message.endpoint as StartedWorkload);
            if (message.type === 'failed') integrationFailure = new Error(`Integration failed: ${JSON.stringify(message)}`);
        }
    });
    const ready = waitForMessage(worker, 'ready', 60_000);
    worker.send({ port, token, password, ...invitations, configurationUpdates: true });
    const confirmationWork = (async () => {
        if (dashboard) {
            await dashboard.confirm();
        } else {
            const end = Date.now() + 45_000;
            while (true) {
                if (integrationFailure) throw integrationFailure;
                const pending = await observer.readConfirmations();
                const confirmation = pending.requests?.[0];
                if (confirmation?.requestId) {
                    await observer.respondConfirmation(confirmation.requestId, true);
                    break;
                }
                assert.ok(Date.now() < end, 'Integration confirmation timed out.');
                await delay(100);
            }
        }
    })();
    const [message] = await Promise.all([ready, confirmationWork]);
    const original = message as Ready;
    assert.equal(original.type, 'ready');
    await waitHealthy(observer, ['cache', 'database', 'tunnel']);
    const initial = await observer.readResourceObservations();
    const workerPid = worker.pid;

    await executeAppHost({ proofValue: 'must-not-apply', discard: 'abort' });
    assert.deepEqual((await observer.readResourceObservations()).resources, initial.resources);
    await executeAppHost({ proofValue: 'must-not-apply', discard: 'disconnect' });
    assert.deepEqual((await observer.readResourceObservations()).resources, initial.resources);
    await executeAppHost({});
    await waitHealthy(observer, ['cache', 'database', 'tunnel']);
    assert.deepEqual((await observer.readResourceObservations()).resources, initial.resources);
    assert.ok(containerExists(original.cache) && containerExists(original.database));
    assert.equal(await readValue(observer), 'native-value');
    assert.ok(original.relay.instanceId);
    process.kill(Number(original.relay.instanceId), 0);

    await executeAppHost({ proofValue: 'reloaded-value' });
    await waitHealthy(observer, ['cache', 'database', 'tunnel']);
    assert.equal(await readValue(observer), 'reloaded-value');
    assert.ok(containerExists(original.cache) && containerExists(original.database));
    assert.equal(updates.get('cache')?.instanceId, original.cache.instanceId);

    await executeAppHost({ proofValue: 'restarted-value', maxmemory: '16mb' });
    await waitHealthy(observer, ['cache', 'database', 'tunnel']);
    assert.equal(await readValue(observer), 'restarted-value');
    const replacementCache = updates.get('cache');
    const replacementRelay = updates.get('tunnel');
    assert.ok(replacementCache && replacementRelay);
    assert.notEqual(replacementCache.instanceId, original.cache.instanceId);
    assert.notEqual(replacementRelay.instanceId, original.relay.instanceId);
    assert.ok(!containerExists(original.cache) && containerExists(replacementCache) && containerExists(original.database));
    assert.ok(replacementRelay.host && replacementRelay.port);
    assert.equal(await redisCommand(replacementRelay.host, replacementRelay.port, ['GET', 'native-proof']), 'restarted-value');
    const revised = await observer.readResourceObservations();
    assert.deepEqual(revised.resources?.map(resource => resource.resourceId), initial.resources?.map(resource => resource.resourceId));
    await dashboard?.verifyResources();

    await expectRetirement(['tunnel']);
    await executeAppHost({ proofValue: 'restarted-value', maxmemory: '16mb', removeTunnel: true });
    await waitHealthy(observer, ['cache', 'database']);
    await dashboard?.page.getByRole('row').filter({ hasText: 'tunnel' }).waitFor({ state: 'detached', timeout: 30_000 });
    assert.ok(replacementRelay.instanceId);
    assert.throws(() => process.kill(Number(replacementRelay.instanceId), 0),
        (error: unknown) => error instanceof Error && 'code' in error && error.code === 'ESRCH');
    const database = new pg.Client({
        host: original.database.host, port: original.database.port, user: 'postgres', password, database: 'postgres',
        connectionTimeoutMillis: 2000, query_timeout: 5000
    });
    try {
        await database.connect();
        assert.deepEqual((await database.query('SELECT value FROM native_proof')).rows, [{ value: 'native-value' }]);
    } finally { await database.end(); }
    assert.equal(worker.pid, workerPid);
    process.kill(workerPid!, 0);
    process.kill(server.pid!, 0);
    await expectRetirement(['cache', 'database']);
    await executeAppHost({ retire: true });
    assert.ok(!containerExists(replacementCache) && !containerExists(original.database));
    console.log(JSON.stringify({
        appHostProcessExecutions: executions,
        invalidAndAbandonedStagingDoesNotChangeExecution: 'passed',
        serverAndIntegrationProcessRetained: 'passed',
        unchangedWorkloadsAndDatabaseDataRetained: 'passed',
        inPlaceConfigurationUpdate: 'passed',
        integrationControlledContainerRestart: 'passed',
        dependentEndpointInvalidationAndRelayRestart: 'passed',
        stableExecutionIdsAcrossRevisions: 'passed',
        removedResourceAndFinalWorkloadsCleanedUp: 'passed',
        actualDashboardAcrossRevisions: dashboard ? 'passed' : 'not exercised'
    }, null, 2));
} finally {
    for (const child of authors) {
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
    }
    if (worker && worker.exitCode === null && worker.signalCode === null) {
        const exit = once(worker, 'exit');
        if (worker.connected) worker.send('stop');
        const timeout = setTimeout(() => worker!.kill('SIGKILL'), 10_000);
        try { await exit; } finally { clearTimeout(timeout); }
    }
    await dashboard?.dispose();
    observerClient?.disconnect();
    if (server.exitCode === null && server.signalCode === null) {
        const exit = once(server, 'exit');
        server.kill('SIGINT');
        try { await exit; } finally { clearTimeout(deadline); }
    } else clearTimeout(deadline);
}
