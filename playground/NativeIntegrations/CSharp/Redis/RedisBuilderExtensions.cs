// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Authoring;

namespace NativeIntegrations.Redis.Authoring;

/// <summary>Authors Redis by composing ordinary generic hosting primitives.</summary>
public static class RedisBuilderExtensions
{
    /// <summary>Adds authenticated run-only Redis with a native-generated credential by default.</summary>
    public static IResourceBuilder<RedisContainerResource> AddRedis(this DistributedApplicationBuilder builder, string name,
        IResourceBuilder<ParameterResource>? password = null)
    {
        password ??= builder.AddParameter($"{name}-password");
        var cache = builder.AddContainer(new RedisContainerResource(name))
            .WithEntrypoint("/bin/sh")
            .WithEnvironment("REDIS_PASSWORD", password)
            // Environment expansion is explicit and quoted; no credential becomes
            // an authoring argument or crosses the portable provider protocol.
            .WithArgs("-c", "exec redis-server --requirepass \"$REDIS_PASSWORD\" --appendonly yes --appendfsync always --dir /data")
            .WithEndpoint("tcp", 6379, "redis");
        var endpoint = cache.GetEndpoint("tcp");
        cache.WithConnectionString(ReferenceExpression.Create(endpoint.HostAndPort, ",password=", password.AsReference()));
        cache.WithHealthCheck(new ExecutableCommand(
            Environment.ProcessPath ?? throw new InvalidOperationException("Redis health requires the provider executable."),
            AppContext.BaseDirectory, "--redis-health")
            .WithEnvironment("REDIS_HOST", endpoint.Host)
            .WithEnvironment("REDIS_PORT", endpoint.Port)
            .WithEnvironment("REDIS_PASSWORD", password.AsReference()));

        return cache;
    }

}
