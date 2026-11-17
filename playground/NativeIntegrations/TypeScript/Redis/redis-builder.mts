// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { DistributedApplicationBuilder, NativeBuildContext, ParameterResource, ResourceBuilder } from './native-builder.mjs';
import { IntegrationClient } from './provider-client.mjs';
import { RedisContainerResource, RedisResourceBuilder } from './redis-authoring.mjs';

export { createBuilder } from './native-builder.mjs';

declare module './native-builder.mjs' {
    interface DistributedApplicationBuilder {
        addRedis(name: string, password?: ResourceBuilder<ParameterResource>): RedisResourceBuilder;
    }
}

// Importing the integration installs its typed authoring contribution. Generated
// projection calls remain private to this bridge, not ordinary AppHosts.
DistributedApplicationBuilder.prototype.addRedis = function (this: DistributedApplicationBuilder, name: string,
    password = this.addParameterWithGeneratedValue(`${name}-password`, {}, { secret: true })) {
    const credential = password.asReference();
    const cache = this.addIntegrationResource(new RedisContainerResource(name), 'example.ts.redis', async context => {
        const provider = new IntegrationClient(context.client);
        const resource = await provider.addRedis(context.composition, name, await context.value(credential));
        return { declaration: await resource.redisDeclaration(), connection: NativeBuildContext.reference(await resource.redisConnection()) };
    });
    return new RedisResourceBuilder(this, cache.resource);
};
