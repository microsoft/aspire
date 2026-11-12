// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { connectNativeAppHost } from './native-client.mjs';
import type { RpcEnvironmentEditor, RpcResource } from './aspire.mjs';
import { CapabilityError, getCallbackCount } from './transport.mjs';

const { client, server } = await connectNativeAppHost({
    endpoint: { host: '127.0.0.1', port: Number(process.argv[2]) },
    authenticationToken: process.env.ASPIRE_NATIVE_RPC_TOKEN!,
    authenticationTimeoutMilliseconds: 10_000
});
try {
    const callbackCount = getCallbackCount();
    const builder = await server.createRpcBuilder();
    // Integration helpers consume projected RPC handles, not a local graph or
    // a second SDK implementation. Fluent calls come from the ATS generator.
    async function addCache(name: string): Promise<RpcResource> {
        return await builder.addContainer(name, 'redis')
            .withImageTag('8-alpine')
            .withArgs(['--save', ''])
            .withEndpoint({ name: 'tcp', targetPort: 6379, scheme: 'redis' })
            .withSerializedAnnotation('example.cache/v1', '{"version":1}');
    }
    const cache = await addCache('cache');
    const password = await builder.addSecretParameter('password', 'native-private-secret');
    const endpoint = await cache.getEndpoint('tcp');
    await cache.withConnectionString(endpoint.property('hostAndPort'));
    const consumer = await builder.addContainer('consumer', 'example/consumer:1')
        .withReference(cache)
        .withEnvironment('LITERAL', 'literal')
        .withEnvironment('PASSWORD', password)
        .withEnvironment('CACHE_URL', endpoint)
        .waitFor(cache);
    let retained: RpcEnvironmentEditor | undefined;
    await consumer.withEnvironmentCallback(async environment => {
        retained = environment;
        await environment.set('EDITED', 'callback');
        await environment.set('SYMBOLIC', endpoint.property('host'));
        assert.equal(await cache.getSerializedAnnotation('example.cache/v1'), '{"version":1}');
    }).withArgsCallback(async args => {
        await args.add('--endpoint');
        await args.add(endpoint.property('hostAndPort'));
    }).withSerializedAnnotation('example.consumer/v1', '{"configured":true}');
    assert.equal(getCallbackCount(), callbackCount);
    assert.ok(retained);
    // Generated fluent promises are also tracked by the TS client. Expected
    // failures are drained explicitly rather than poisoning a later flush.
    client.throwOnPendingRejections = false;
    try {
        await assert.rejects(async () => await retained!.set('STALE', 'must-not-apply'),
            (error: unknown) => error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND');
        await client.flushPendingPromises();
    } finally {
        client.throwOnPendingRejections = true;
    }
    client.throwOnPendingRejections = false;
    try {
        await assert.rejects(async () => await consumer.withEnvironmentCallback(async environment => {
            await environment.set('PARTIAL', 'retained');
            throw new Error('private-callback-secret');
        }), (error: unknown) => error instanceof CapabilityError &&
            error.code === 'PROVIDER_FAILED' && error.message === 'The provider rejected the invocation.');
        await client.flushPendingPromises();
    } finally {
        client.throwOnPendingRejections = true;
    }
    const composition = await builder.build();
    const snapshot = await composition.inspect();
    assert.ok(snapshot.resources);
    assert.deepEqual(snapshot.resources.map(resource => resource.name), ['cache', 'consumer']);
    assert.deepEqual(snapshot.resources[1].dependencies, [snapshot.resources[0].resourceId]);
    assert.equal(await consumer.getSerializedAnnotation('example.consumer/v1'), '{"configured":true}');
    assert.equal(await consumer.getSerializedAnnotation('missing'), null);
    await builder.retire();
    client.throwOnPendingRejections = false;
    try {
        await assert.rejects(async () => await consumer.withEnvironmentCallback(async () => {}),
            (error: unknown) => error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND');
        await client.flushPendingPromises();
    } finally {
        client.throwOnPendingRejections = true;
    }
    assert.equal(getCallbackCount(), callbackCount);
    console.log('rpc-authoring-ok');
} finally {
    await client.flushPendingPromises();
    client.disconnect();
}
