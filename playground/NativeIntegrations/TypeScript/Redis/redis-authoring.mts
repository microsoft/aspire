// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { fileURLToPath } from 'node:url';
import { ContainerResource, DistributedApplicationBuilder, ExecutableCommand, NativeBuildContext,
    ParameterResource, ReferenceExpression, ResourceBuilder } from './native-builder.mjs';

/** Inert Redis resource data, separate from provider runtime lifecycle state. */
export class RedisContainerResource extends ContainerResource {
    constructor(name: string) { super(name, 'redis:7.4-alpine'); }
    get connectionStringExpression(): ReferenceExpression { return NativeBuildContext.connectionReference(this); }
}
export class RedisResourceBuilder extends ResourceBuilder<RedisContainerResource> {
    withDataVolume(name = this.applicationBuilder.getDefaultDataVolumeName(this.resource)): this {
        return this.withVolume(name, '/data');
    }
}

/** Integration authoring uses the same generic builder/container primitives as ordinary composition. */
export function addRedis(builder: DistributedApplicationBuilder, name: string,
    password = builder.addParameterWithGeneratedValue(`${name}-password`, {}, { secret: true })): RedisResourceBuilder {
    const cache = builder.addContainer(new RedisContainerResource(name))
        .withEntrypoint('/bin/sh')
        .withEnvironment('REDIS_PASSWORD', password)
        // Explicit quoted shell expansion keeps the credential native-owned.
        .withArgs('-c', 'exec redis-server --requirepass "$REDIS_PASSWORD" --appendonly yes --appendfsync always --dir /data')
        .withEndpoint('tcp', 6379, 'redis');
    const endpoint = cache.getEndpoint('tcp');
    cache.withConnectionString(ReferenceExpression.create(endpoint.hostAndPort, ',password=', password.asReference()));
    cache.withHealthCheck(new ExecutableCommand(process.execPath, fileURLToPath(new URL('.', import.meta.url)),
        [fileURLToPath(new URL('./redis-provider.mjs', import.meta.url)), '--redis-health'])
        .withEnvironment('REDIS_HOST', endpoint.host)
        .withEnvironment('REDIS_PORT', endpoint.port)
        .withEnvironment('REDIS_PASSWORD', password.asReference()));
    return new RedisResourceBuilder(builder, cache.resource);
}
