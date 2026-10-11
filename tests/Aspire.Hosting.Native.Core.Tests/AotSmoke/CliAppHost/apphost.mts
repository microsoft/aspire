// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { fork } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { connectNativeAppHost } from './.aspire/modules/native-client.mjs';

const socketPath = process.env.REMOTE_APP_HOST_SOCKET_PATH;
const authenticationToken = process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
const resultsPath = process.env.ASPIRE_NATIVE_CLI_RESULTS_PATH;
assert.ok(socketPath && authenticationToken && resultsPath);
const stopped = new Promise<void>(resolve => {
    process.once('SIGINT', resolve);
    process.once('SIGTERM', resolve);
});
const { client, server } = await connectNativeAppHost({ endpoint: socketPath, authenticationToken });
const session = await server.openApplication();
const composition = await session.startGeneration();
let worker: ReturnType<typeof fork> | undefined;
try {
    const cache = await composition.addResource('cache', 'native.testing/Redis');
    const database = await composition.addResource('database', 'native.testing/Postgres');
    const tunnel = await composition.addResource('tunnel', 'native.testing/LocalTunnel');
    await tunnel.waitFor(cache);
    const execution = await composition.createExecution();
    const observer = await server.joinApplicationObserver(await execution.inviteApplicationObserver());
    worker = fork(fileURLToPath(new URL('./out/NativeIntegrationWorker.mjs', import.meta.url)), [], {
        stdio: ['ignore', 'inherit', 'inherit', 'ipc']
    });
    const ready = Promise.race([
        once(worker, 'message'),
        once(worker, 'exit').then(([code]) => { throw new Error(`Integration host exited before readiness (${code}).`); })
    ]);
    worker.send({
        socketPath, port: 0, token: authenticationToken, password: randomBytes(24).toString('hex'),
        redisInvitation: await execution.inviteResourceExecution(cache),
        postgresInvitation: await execution.inviteResourceExecution(database),
        tunnelInvitation: await execution.inviteResourceExecution(tunnel)
    });
    let initialized = false;
    const workloads = ready.then(async ([message]) => {
        if (message && typeof message === 'object' && 'type' in message && message.type === 'failed') {
            for (const resource of (await observer.readResourceObservations()).resources ?? []) {
                assert.ok(resource.resourceId);
                for (const line of (await observer.readResourceLogs(resource.resourceId, 0)).entries ?? []) {
                    console.error(`[${resource.name}] ${line.stream}: ${line.message}`);
                }
            }
        }
        assert.ok(message && typeof message === 'object' && 'type' in message && message.type === 'ready',
            `Integration host failed: ${JSON.stringify(message)}`);
        initialized = true;
        return message;
    });
    const confirm = (async () => {
        if (process.env.ASPIRE_NATIVE_DASHBOARD_DLL) {
            return;
        }
        while (!initialized) {
            for (const request of (await observer.readConfirmations()).requests ?? []) {
                assert.ok(request.requestId);
                await observer.respondConfirmation(request.requestId, true);
            }
            const snapshot = await observer.readResourceObservations();
            assert.ok(snapshot.version !== undefined);
            await observer.waitResourceObservations(snapshot.version, 1000);
        }
    })();
    const [instances] = await Promise.all([workloads, confirm]);
    const observations = await observer.readResourceObservations();
    assert.deepEqual(observations.resources?.map(resource => [resource.name, resource.state, resource.healthy]), [
        ['cache', 'Running', true], ['database', 'Running', true], ['tunnel', 'Running', true]
    ]);
    const result = await (await observer.invokeResourceCommand('cache', 'read-value')).awaitRuntimeOperation();
    assert.deepEqual(result, { status: 'succeeded', message: 'native-value' });
    await client.flushPendingPromises();
    await writeFile(resultsPath, JSON.stringify({ instances, observations, command: result }, null, 2));
    console.log('Native production CLI resources ready.');
    await stopped;
} finally {
    try {
        if (worker && worker.exitCode === null && worker.signalCode === null) {
            const exited = once(worker, 'exit');
            if (worker.connected) worker.send('stop', error => {
                if (error && (!('code' in error) || !['EPIPE', 'ERR_IPC_CHANNEL_CLOSED'].includes(String(error.code)))) {
                    console.error(error);
                    worker?.kill('SIGKILL');
                }
            });
            const deadline = setTimeout(() => worker?.kill('SIGKILL'), 10_000);
            try {
                await exited;
            } finally {
                clearTimeout(deadline);
            }
        }
        await session.retireGeneration(composition);
    } finally {
        client.disconnect();
    }
}
