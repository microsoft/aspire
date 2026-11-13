// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Authoring;
using NativeIntegration.Generated;

namespace NativeIntegrations.Redis.AppHost;

/// <summary>Projects typed AppHost composition to the generated out-of-process Redis provider.</summary>
public static class RedisBuilderExtensions
{
    /// <summary>Records authenticated Redis authoring without connecting, activating or resolving secrets.</summary>
    public static IResourceBuilder<RedisContainerResource> AddRedis(this DistributedApplicationBuilder builder, string name,
        IResourceBuilder<ParameterResource>? password = null)
    {
        password ??= builder.AddParameter($"{name}-password");
        var credential = password.AsReference();

        return builder.AddIntegrationResource(new RedisContainerResource(name), "example.csharp.redis", async context =>
        {
            var cache = await context.Composition.AddRedisAsync(name, await context.Value(credential).ConfigureAwait(false), context.Token).ConfigureAwait(false);
            var declaration = await cache.GetDeclarationAsync(context.Token).ConfigureAwait(false);
            var connection = await cache.GetConnectionAsync(context.Token).ConfigureAwait(false);

            return (declaration, NativeBuildContext.Reference(connection));
        });
    }
}
