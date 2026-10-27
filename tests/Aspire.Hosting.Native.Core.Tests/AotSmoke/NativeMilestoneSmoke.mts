// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { createConnection } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { connectNativeAppHost } from './native-client.mjs';
import type { AspireClient } from './transport.mjs';
import { CapabilityError } from './transport.mjs';
import type { Composition } from './aspire.mjs';

const binary = process.argv[2];
assert.ok(binary && process.env.ASPIRE_NATIVE_DCP_PATH, 'Supply the published native server and ASPIRE_NATIVE_DCP_PATH.');
const token = randomBytes(32).toString('hex');
const server = spawn(binary, ['--port', '0'], {
    env: { ...process.env, ASPIRE_NATIVE_RPC_TOKEN: token },
    stdio: ['ignore', 'pipe', 'inherit']
});
const deadline = setTimeout(() => server.kill('SIGKILL'), 120_000);
let client: AspireClient | undefined;
try {
    const lines = createInterface({ input: server.stdout });
    const [line] = await Promise.race([
        once(lines, 'line'),
        once(server, 'exit').then(([code]) => { throw new Error(`Native server exited during startup (${code}).`); })
    ]);
    lines.close();
    server.stdout.pipe(process.stderr);
    // Direct native launches announce transport coordinates as:
    //   Native AppHost server listening on 127.0.0.1:<allocated-port>.
    const match = /^Native AppHost server listening on 127\.0\.0\.1:(\d+)\.$/.exec(String(line));
    assert.ok(match);
    const connected = await connectNativeAppHost({
        endpoint: { host: '127.0.0.1', port: Number(match[1]) },
        authenticationToken: token,
        authenticationTimeoutMilliseconds: 10_000
    });
    client = connected.client;
    const builder = await connected.server.createRpcBuilder();
    const worker = fileURLToPath(new URL('./MilestonePeer.mjs', import.meta.url));
    const alpha = await builder.addExecutable('alpha', process.execPath, process.cwd())
        .withArgs([worker, 'alpha'])
        .withEndpoint({ name: 'http', targetPort: 0, scheme: 'http', env: 'PORT' });
    const beta = await builder.addExecutable('beta', process.execPath, process.cwd())
        .withArgs([worker, 'beta'])
        .withEndpoint({ name: 'http', targetPort: 0, scheme: 'http', env: 'PORT' });
    const alphaEndpoint = await alpha.getEndpoint('http');
    const betaEndpoint = await beta.getEndpoint('http');
    await alpha.withConnectionString(alphaEndpoint)
        .withEnvironment('SELF_URL', alphaEndpoint)
        .withEnvironment('PEER_URL', betaEndpoint);
    await beta.withConnectionString(betaEndpoint)
        .withEnvironment('SELF_URL', betaEndpoint)
        .withEnvironment('PEER_URL', alphaEndpoint);
    await alpha.withReference(beta).withArgsCallback(async args => {
        await args.add(betaEndpoint);
    });
    await beta.withReference(alpha).withArgsCallback(async args => {
        await args.add(alphaEndpoint);
    });
    const composition = await builder.build();
    const snapshot = await composition.inspect();
    assert.deepEqual(snapshot.resources?.map(resource => resource.dependencies), [[], []]);
    const execution = await composition.startApplication();
    let retiredAddresses: string[] = [];
    try {
        await execution.waitApplicationReady(30_000);
        const state = await execution.readApplicationState();
        const addresses = new Map(state.resources?.map(resource => [resource.name!, resource.urls![0]!]));
        const alphaUrl = addresses.get('alpha');
        const betaUrl = addresses.get('beta');
        assert.ok(alphaUrl && betaUrl && alphaUrl !== betaUrl);
        retiredAddresses = [alphaUrl, betaUrl];
        const readiness = AbortSignal.timeout(15_000);
        async function identity(address: string): Promise<unknown> {
            while (true) {
                try {
                    const response = await fetch(`${address}/identity`, { signal: readiness });
                    assert.equal(response.status, 200);
                    return await response.json();
                } catch (error) {
                    if (!(error instanceof TypeError) || readiness.aborted) throw error;
                    await delay(50, undefined, { signal: readiness });
                }
            }
        }
        assert.deepEqual(await Promise.all([identity(alphaUrl), identity(betaUrl)]), [
            { name: 'alpha', self: alphaUrl, peer: betaUrl },
            { name: 'beta', self: betaUrl, peer: alphaUrl }
        ]);
        const peers = await Promise.all([alphaUrl, betaUrl].map(async address => {
            const response = await fetch(`${address}/peer`, { signal: AbortSignal.timeout(5000) });
            assert.equal(response.status, 200);
            return response.json();
        }));
        assert.deepEqual(peers, [
            { name: 'beta', self: betaUrl, peer: alphaUrl },
            { name: 'alpha', self: alphaUrl, peer: betaUrl }
        ]);
        console.log(JSON.stringify({ mutualReferences: true, selfReferences: true, symbolicArguments: true, alphaUrl, betaUrl }));
    } finally {
        await builder.retire();
    }
    await Promise.all(retiredAddresses.map(address => assert.rejects(
        fetch(`${address}/identity`, { signal: AbortSignal.timeout(2000) }),
        (error: unknown) => error instanceof TypeError)));

    const workspace = await connected.server.createApplicationWorkspace();
    async function declareRevision(composition: Composition, version: string): Promise<void> {
        const first = await composition.addExecutable('alpha', process.execPath, process.cwd());
        const second = await composition.addExecutable('beta', process.execPath, process.cwd());
        for (const [resource, name] of [[first, 'alpha'], [second, 'beta']] as const) {
            await resource.withArgument(await composition.createLiteralValue(worker));
            await resource.withArgument(await composition.createLiteralValue(name));
            await resource.withEndpoint({
                name: 'http', protocol: 'TCP', uriScheme: 'http', targetPort: 0, hostPort: 0, environmentVariable: 'PORT'
            });
            await resource.withEnvironment('REVISION', await composition.createLiteralValue(name === 'alpha' ? version : 'stable'));
        }
        const firstUrl = await composition.createEndpointValue(first, 'http', 'url');
        const secondUrl = await composition.createEndpointValue(second, 'http', 'url');
        await first.withEnvironment('SELF_URL', firstUrl);
        await first.withEnvironment('PEER_URL', secondUrl);
        await first.withEnvironment('ConnectionStrings__beta', secondUrl);
        await first.withArgument(secondUrl);
        await second.withEnvironment('SELF_URL', secondUrl);
        await second.withEnvironment('PEER_URL', firstUrl);
        await second.withEnvironment('ConnectionStrings__alpha', firstUrl);
        await second.withArgument(firstUrl);
    }
    try {
        for (const version of ['one', 'two']) {
            const revision = await workspace.beginRevision();
            await declareRevision(revision, version);
            await workspace.commitRevision(revision);
            const application = version === 'one'
                ? await revision.startApplication()
                : await workspace.getApplicationExecution();
            await application.waitApplicationReady(30_000);
            const resources = (await application.readApplicationState()).resources!;
            const addresses = new Map(resources.map(resource => [resource.name!, resource.urls![0]!]));
            const readiness = AbortSignal.timeout(15_000);
            await Promise.all(['alpha', 'beta'].map(async name => {
                const self = addresses.get(name)!;
                const peer = addresses.get(name === 'alpha' ? 'beta' : 'alpha')!;
                while (true) {
                    try {
                        const response = await fetch(`${self}/identity`, { signal: readiness });
                        assert.equal(response.status, 200);
                        assert.deepEqual(await response.json(), { name, self, peer, revision: name === 'alpha' ? version : 'stable' });
                        return;
                    } catch (error) {
                        if (!(error instanceof TypeError) || readiness.aborted) throw error;
                        await delay(50, undefined, { signal: readiness });
                    }
                }
            }));
            await Promise.all(['alpha', 'beta'].map(async name => {
                const peerName = name === 'alpha' ? 'beta' : 'alpha';
                const response = await fetch(`${addresses.get(name)!}/peer`, { signal: readiness });
                assert.equal(response.status, 200);
                assert.deepEqual(await response.json(), {
                    name: peerName, self: addresses.get(peerName), peer: addresses.get(name),
                    revision: peerName === 'alpha' ? version : 'stable'
                });
            }));
        }
        console.log('Reference-cycle workspace revision reallocated both peers without stale configuration.');
    } finally {
        await workspace.retireApplicationWorkspace();
    }

    const containers = await connected.server.createRpcBuilder();
    try {
        await containers.addContainer('cache', 'docker.io/library/redis:8.2-alpine')
            .withEndpoint({ name: 'tcp', targetPort: 6379, scheme: 'redis' });
        const application = await (await containers.build()).startApplication();
        await application.waitApplicationReady(30_000);
        const address = new URL((await application.readApplicationState()).resources![0]!.urls![0]!);
        const readiness = AbortSignal.timeout(15_000);
        while (true) {
            const socket = createConnection({ host: address.hostname, port: Number(address.port), signal: readiness });
            try {
                await once(socket, 'connect');
                const pong = new Promise<string>((resolve, reject) => {
                    let response = '';
                    socket.on('data', bytes => {
                        response += bytes.toString('utf8');
                        if (response.endsWith('\r\n')) resolve(response);
                    });
                    socket.once('error', reject);
                    socket.once('end', () => reject(Object.assign(new Error('Redis closed before replying.'), { code: 'ECONNRESET' })));
                });
                socket.write('*1\r\n$4\r\nPING\r\n');
                assert.equal(await pong, '+PONG\r\n');
                console.log('Prepared container endpoint routes a real Redis PING through the allocated DCP proxy.');
                break;
            } catch (error) {
                // DCP process readiness does not imply Redis has bound its port.
                // Retry only transport startup failures within the probe budget.
                if (!(error instanceof Error) || readiness.aborted || !('code' in error) ||
                    !['ECONNREFUSED', 'ECONNRESET', 'EPIPE'].includes(String(error.code))) throw error;
                await delay(50, undefined, { signal: readiness });
            } finally {
                socket.destroy();
            }
        }
    } finally {
        await containers.retire();
    }
    const cycle = await connected.server.createRpcBuilder();
    try {
        const first = await cycle.addExecutable('first', process.execPath, process.cwd());
        const second = await cycle.addExecutable('second', process.execPath, process.cwd());
        await first.waitFor(second);
        client.throwOnPendingRejections = false;
        try {
            await assert.rejects(async () => { await second.waitFor(first); }, (error: unknown) =>
                error instanceof CapabilityError && error.code === 'EXECUTION_PREREQUISITE_CYCLE' &&
                error.message === 'Execution prerequisite cycle: second.Start -> first.Ready -> first.Start -> second.Ready -> second.Start');
            await client.flushPendingPromises();
        } finally {
            client.throwOnPendingRejections = true;
        }
    } finally {
        await cycle.retire();
    }
    console.log('NativeAOT/DCP milestone cycle E2E passed: mutual endpoint references succeed; a readiness cycle is rejected.');
} finally {
    await client?.disconnect();
    if (server.exitCode === null && server.signalCode === null) {
        const exited = once(server, 'exit');
        server.kill('SIGTERM');
        await exited;
    }
    clearTimeout(deadline);
}
