// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { fileURLToPath } from 'node:url';
import { AspireExport, defineIntegration } from './base.mjs';
import type { Composition, DeclaredResource, DeclaredValue, ResourceLifecycleScope } from './aspire.mjs';
import { getIntegrationInvocationSignal, IntegrationResource } from './integration-host.mjs';
import { nativeTypes } from './native-types.mjs';
import { DistributedApplicationBuilder } from './native-builder.mjs';
import { addRedis as authorRedis } from './redis-authoring.mjs';

const resourceType = { typeId: 'example.ts.redis/RedisResource', category: 'Handle' } as const;
const text = { typeId: 'string', category: 'Primitive' } as const;

/** Redis-owned API over a generic native container and symbolic connection. */
export class RedisResource extends IntegrationResource {
    private lifetime?: AbortController;
    constructor(readonly nativeDeclaration: DeclaredResource, readonly connection: DeclaredValue) {
        super(nativeDeclaration, resourceType.typeId);
    }
    initialize(): void { this.lifetime = new AbortController(); }
    override async dispose(): Promise<void> {
        this.lifetime?.abort();
        this.lifetime = undefined;
    }
}

export const addRedis = AspireExport({
    id: 'example.ts.redis/addRedis', method: 'addRedis',
    description: 'Declares authenticated run-only Redis and native protocol health.',
    projection: {
        parameters: [{ name: 'composition', type: nativeTypes.Composition }, { name: 'name', type: text },
            { name: 'password', type: nativeTypes.DeclaredValue }],
        returnType: resourceType
    }
}, async (args: { composition: Composition; name: string; password: DeclaredValue }) => {
    const { composition, name, password } = args;
    const signal = getIntegrationInvocationSignal(args);
    const builder = new DistributedApplicationBuilder();
    const credential = builder.importParameter(`${name}-password`, password);
    const cache = authorRedis(builder, name, credential);
    const native = await builder.materialize(composition, undefined, signal);
    return new RedisResource(native.declaration(cache.resource), await native.value(cache.resource.connectionStringExpression));
});

const declaration = AspireExport({
    id: 'example.ts.redis/redisDeclaration', method: 'redisDeclaration', description: 'Original delegated native declaration.',
    projection: { targetTypeId: resourceType.typeId, targetParameterName: 'context',
        parameters: [{ name: 'context', type: resourceType }], returnType: nativeTypes.DeclaredResource }
}, async ({ context }: { context: RedisResource }) => context.nativeDeclaration);
const connection = AspireExport({
    id: 'example.ts.redis/redisConnection', method: 'redisConnection', description: 'Native symbolic connection, never credential contents.',
    projection: { targetTypeId: resourceType.typeId, targetParameterName: 'context',
        parameters: [{ name: 'context', type: resourceType }], returnType: nativeTypes.DeclaredValue }
}, async ({ context }: { context: RedisResource }) => context.connection);

export const withDataVolume = AspireExport({
    id: 'example.ts.redis/withDataVolume', method: 'withDataVolume',
    description: 'Mounts retained Redis AOF data at /data.',
    projection: { targetTypeId: resourceType.typeId, targetParameterName: 'context',
        parameters: [{ name: 'context', type: resourceType }, { name: 'name', type: text }], returnType: resourceType }
}, async ({ context, name }: { context: RedisResource; name: string }) => {
    await context.nativeDeclaration.withVolume(name, '/data', false);
    return context;
});

export const withReference = AspireExport({
    id: 'example.ts.redis/withReference', method: 'withReference',
    description: 'References the late-bound Redis connection from a host executable.',
    projection: { targetTypeId: resourceType.typeId, targetParameterName: 'context',
        parameters: [{ name: 'context', type: resourceType }, { name: 'consumer', type: nativeTypes.DeclaredResource },
            { name: 'environmentName', type: text }], returnType: nativeTypes.DeclaredResource }
}, async ({ context, consumer, environmentName }: { context: RedisResource; consumer: DeclaredResource; environmentName: string }) => {
    await consumer.withReference(environmentName, context.connection);
    return consumer;
});

const lifecycle = (phase: 'initialize' | 'allocated' | 'ready' | 'retiring', method: string,
    action: (context: RedisResource, scope: ResourceLifecycleScope, signal: AbortSignal) => Promise<void>) =>
    AspireExport({
        id: `example.ts.redis/${method}`, method, description: `Redis-owned ${phase} lifecycle.`,
        projection: {
            targetTypeId: resourceType.typeId, targetParameterName: 'context', lifecyclePhase: phase,
            parameters: [{ name: 'context', type: resourceType }, { name: 'scope', type: nativeTypes.ResourceLifecycleScope }],
            returnType: { typeId: 'void', category: 'Primitive' }
        }
    }, async (args: { context: RedisResource; scope: ResourceLifecycleScope }) => {
        const { context, scope } = args;
        const signal = getIntegrationInvocationSignal(args);
        signal.throwIfAborted();
        await action(context, scope, signal);
    });

const initialize = lifecycle('initialize', 'redisInitialize', async (context, scope) => {
    context.initialize();
    await scope.writeLifecycleLog('stdout', 'Redis integration initialized.');
});
const allocated = lifecycle('allocated', 'redisAllocated', async (_context, scope) => {
    await scope.writeLifecycleLog('stdout', 'Redis endpoint allocated.');
});
const ready = lifecycle('ready', 'redisReady', async (_context, scope) => {
    await scope.writeLifecycleLog('stdout', 'Redis authenticated and ready.');
});
const retiring = lifecycle('retiring', 'redisRetiring', async (_context, scope) => {
    if (!await scope.runLifecycleExecutable({
        executablePath: process.execPath, workingDirectory: fileURLToPath(new URL('.', import.meta.url)),
        arguments: [fileURLToPath(new URL('./redis-provider.mjs', import.meta.url)), '--redis-save'],
        environment: [], endpoints: []
    }, { environment: [
        { name: 'REDIS_HOST', configurationKey: 'health.env.REDIS_HOST' },
        { name: 'REDIS_PORT', configurationKey: 'health.env.REDIS_PORT' },
        { name: 'REDIS_PASSWORD', configurationKey: 'health.env.REDIS_PASSWORD' }
    ], arguments: [] })) {
        throw new Error('Redis final authenticated snapshot failed.');
    }
});

export const integration = defineIntegration({ name: 'Redis',
    capabilities: [addRedis, declaration, connection, withDataVolume, withReference, initialize, allocated, ready, retiring] });
