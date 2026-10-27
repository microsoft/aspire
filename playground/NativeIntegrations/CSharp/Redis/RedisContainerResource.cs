// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Authoring;

namespace NativeIntegrations.Redis;

/// <summary>Contains Redis authoring data; the native server owns running state and secret resolution.</summary>
public sealed class RedisContainerResource(string name) : ContainerResource(name, "redis:7.4-alpine"), IResourceWithConnectionString
{
    /// <summary>Gets a native-resolved symbolic connection reference.</summary>
    public ReferenceExpression ConnectionStringExpression => NativeBuildContext.ConnectionReference(this);
}

/// <summary>Provides Redis-specific fluent configuration for provider and AppHost builders.</summary>
public static class RedisResourceBuilderExtensions
{
    /// <summary>Mounts a retained Redis AOF data volume at /data.</summary>
    public static IResourceBuilder<RedisContainerResource> WithDataVolume(this IResourceBuilder<RedisContainerResource> cache,
        string? name = null) => cache.WithVolume(name ?? cache.ApplicationBuilder.GetDefaultDataVolumeName(cache.Resource), "/data");
}
