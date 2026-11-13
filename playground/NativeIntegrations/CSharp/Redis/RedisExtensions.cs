// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.Native.Authoring;
using Aspire.Hosting.Native.Client;
using NativeIntegrations.Redis.Authoring;

namespace NativeIntegrations.Redis;

/// <summary>Adapts generated portable provider calls to the integration's ordinary builder authoring implementation.</summary>
public static class RedisExtensions
{
    /// <summary>Materializes the authored Redis container using original delegated native authority.</summary>
    [AspireExport("addRedis")]
    public static async Task<RedisResource> AddRedis(Composition composition, string name, DeclaredValue password, CancellationToken cancellationToken)
    {
        var builder = DistributedApplicationBuilder.ForIntegration();
        var credential = builder.ImportParameter($"{name}-password", password);
        var cache = builder.AddRedis(name, credential);
        var native = await builder.MaterializeAsync(composition, cancellationToken).ConfigureAwait(false);

        return new RedisResource(native.Declaration(cache.Resource),
            await native.Value(cache.Resource.ConnectionStringExpression).ConfigureAwait(false));
    }
}

/// <summary>Owns portable Redis callbacks; this is deliberately separate from inert authoring data.</summary>
public sealed class RedisResource(DeclaredResource declaration, DeclaredValue connection) : NativeIntegrationResource(declaration)
{
    private CancellationTokenSource? _lifetime;

    /// <summary>Provides original native declaration authority to the generic builder projection.</summary>
    [AspireExport("redisDeclaration")]
    public DeclaredResource GetDeclaration() => Declaration;

    /// <summary>Provides native symbolic connection authority without returning credential payloads.</summary>
    [AspireExport("redisConnection")]
    public DeclaredValue GetConnection() => connection;

    /// <summary>Initializes provider-local state under native lifecycle ownership.</summary>
    [AspireExport("redisInitialize"), NativeResourceLifecycle("initialize")]
    public async Task Initialize(ResourceLifecycleScope scope, CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();
        await scope.WriteLifecycleLogAsync("stdout", "Redis integration initialized.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Observes endpoint allocation with ephemeral native authority.</summary>
    [AspireExport("redisAllocated"), NativeResourceLifecycle("allocated")]
    public Task Allocated(ResourceLifecycleScope scope, CancellationToken cancellationToken) =>
        scope.WriteLifecycleLogAsync("stdout", "Redis endpoint allocated.", cancellationToken);

    /// <summary>Reports readiness after authenticated native health has succeeded.</summary>
    [AspireExport("redisReady"), NativeResourceLifecycle("ready")]
    public Task Ready(ResourceLifecycleScope scope, CancellationToken cancellationToken) =>
        scope.WriteLifecycleLogAsync("stdout", "Redis authenticated and ready.", cancellationToken);

    /// <summary>Executes authenticated SAVE after public revocation, before workload removal.</summary>
    [AspireExport("redisRetiring"), NativeResourceLifecycle("retiring")]
    public async Task Retiring(ResourceLifecycleScope scope, CancellationToken cancellationToken)
    {
        if (!await scope.RunLifecycleExecutableAsync(new ExecutableWorkloadLaunch
        {
            ExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Redis SAVE requires the provider executable."),
            WorkingDirectory = AppContext.BaseDirectory, Arguments = ["--redis-save"], Environment = [], Endpoints = []
        }, new SymbolicLaunchBindings
        {
            Environment =
            [
                new() { Name = "REDIS_HOST", ConfigurationKey = "health.env.REDIS_HOST" },
                new() { Name = "REDIS_PORT", ConfigurationKey = "health.env.REDIS_PORT" },
                new() { Name = "REDIS_PASSWORD", ConfigurationKey = "health.env.REDIS_PASSWORD" }
            ],
            Arguments = []
        }, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Redis's final authenticated snapshot failed.");
        }
    }

    /// <summary>Releases provider-local state exactly once after native retirement acknowledges disposal.</summary>
    public override ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;

        return ValueTask.CompletedTask;
    }

    /// <summary>Retains the low-level provider projection for transport/conformance consumers.</summary>
    [AspireExport("withDataVolume")]
    public async Task<RedisResource> WithDataVolume(string name)
    {
        await Declaration.WithVolumeAsync(name, "/data", false).ConfigureAwait(false);

        return this;
    }

    /// <summary>Retains the low-level connection projection separately from fluent consumer authoring.</summary>
    [AspireExport("withReference")]
    public async Task<DeclaredResource> WithReference(DeclaredResource consumer, string environmentName)
    {
        await consumer.WithReferenceAsync(environmentName, connection).ConfigureAwait(false);

        return consumer;
    }
}
