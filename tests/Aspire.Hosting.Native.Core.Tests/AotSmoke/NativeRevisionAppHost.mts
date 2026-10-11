// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { connectNativeAppHost } from './native-client.mjs';
import { CapabilityError } from './transport.mjs';

interface Revision {
    port: number;
    token: string;
    workspaceInvitation?: string;
    proofValue?: string;
    maxmemory?: string;
    removeTunnel?: boolean;
    retire?: boolean;
    discard?: 'abort' | 'disconnect';
}

process.once('message', configuration => {
    void run(configuration as Revision).catch(error => {
        console.error(error);
        process.exitCode = 1;
        if (process.connected) process.disconnect();
    });
});

async function run(configuration: Revision): Promise<void> {
    assert.ok(process.send);
    const { client, server } = await connectNativeAppHost({
        endpoint: { host: '127.0.0.1', port: configuration.port }, authenticationToken: configuration.token
    });
    try {
        const workspace = configuration.workspaceInvitation
            ? await server.joinApplicationWorkspace(configuration.workspaceInvitation)
            : await server.createApplicationWorkspace();
        if (configuration.retire) {
            await workspace.retireApplicationWorkspace();
            process.send({ type: 'retired' });
            return;
        }
        const revision = await workspace.beginRevision();
        const cache = await revision.addResource('cache', 'native.testing/Redis');
        const database = await revision.addResource('database', 'native.testing/Postgres');
        const tunnel = configuration.removeTunnel ? undefined :
            await revision.addResource('tunnel', 'native.testing/LocalTunnel');
        if (tunnel) await tunnel.waitFor(cache);
        if (configuration.proofValue || configuration.maxmemory) {
            await cache.setResourceConfiguration({
                properties: [
                    ...(configuration.proofValue ? [{ name: 'proofValue', value: configuration.proofValue }] : []),
                    ...(configuration.maxmemory ? [{ name: 'maxmemory', value: configuration.maxmemory }] : [])
                ]
            });
        }
        if (configuration.discard) {
            // A staged readiness cycle is rejected without changing execution.
            await cache.waitFor(database);
            client.throwOnPendingRejections = false;
            try {
                await assert.rejects(async () => await database.waitFor(cache),
                    (error: unknown) => error instanceof CapabilityError && error.code === 'OPERATION_REJECTED');
                await client.flushPendingPromises();
            } finally {
                client.throwOnPendingRejections = true;
            }
            if (configuration.discard === 'abort') await workspace.abortRevision(revision);
            process.send({ type: 'discarded', workspaceInvitation: await workspace.inviteApplicationWorkspace() });
            return;
        }
        await workspace.commitRevision(revision);
        const execution = await workspace.getApplicationExecution();
        process.send({
            type: 'committed',
            workspaceInvitation: await workspace.inviteApplicationWorkspace(),
            observerInvitation: configuration.workspaceInvitation ? undefined : await execution.inviteApplicationObserver(),
            redisInvitation: configuration.workspaceInvitation ? undefined : await execution.inviteResourceExecution(cache),
            postgresInvitation: configuration.workspaceInvitation ? undefined : await execution.inviteResourceExecution(database),
            tunnelInvitation: configuration.workspaceInvitation || !tunnel ? undefined : await execution.inviteResourceExecution(tunnel),
        });
    } finally {
        await client.flushPendingPromises();
        client.disconnect();
        if (process.connected) process.disconnect();
    }
}
