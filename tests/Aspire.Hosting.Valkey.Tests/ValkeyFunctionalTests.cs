// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Aspire.Hosting.Valkey.Tests;

public class ValkeyFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task VerifyValkeyResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var valkey = builder.AddValkey("valkey");

        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{valkey.Resource.Name}"] = await valkey.Resource.ConnectionStringExpression.GetValueAsync(default)
        });

        hb.AddRedisClient(valkey.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await app.WaitForHealthyAsync(valkey).WaitAsync(TestConstants.LongTimeoutTimeSpan);

        var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

        var db = redisClient.GetDatabase();

        await db.StringSetAsync("key", "value");

        var value = await db.StringGetAsync("key");

        Assert.Equal("value", value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var valkey1 = builder1.AddValkey("valkey");

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(valkey1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                valkey1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "data");

                valkey1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    // BGSAVE is only available in admin mode, enable it for this instance
                    hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"ConnectionStrings:{valkey1.Resource.Name}"] = $"{await valkey1.Resource.ConnectionStringExpression.GetValueAsync(default)},allowAdmin=true"
                    });

                    hb.AddRedisClient(valkey1.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await app.WaitForHealthyAsync(valkey1).WaitAsync(TestConstants.LongTimeoutTimeSpan);

                        var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                        var db = redisClient.GetDatabase();

                        await db.StringSetAsync("key", "value");

                        // Force Redis to save the keys (snapshotting)
                        // c.f. https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/

                        await redisClient.GetServers().First().SaveAsync(SaveType.BackgroundSave);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var valkey2 = builder2.AddValkey("valkey");

            if (useVolume)
            {
                valkey2.WithDataVolume(volumeName);
            }
            else
            {
                valkey2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"ConnectionStrings:{valkey2.Resource.Name}"] = await valkey2.Resource.ConnectionStringExpression.GetValueAsync(default)
                    });

                    hb.AddRedisClient(valkey2.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await app.WaitForHealthyAsync(valkey2).WaitAsync(TestConstants.LongTimeoutTimeSpan);

                        var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                        var db = redisClient.GetDatabase();

                        var value = await db.StringGetAsync("key");

                        Assert.Equal("value", value);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }
        }
        finally
        {
            if (volumeName is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName);
            }

            if (bindMountPath is not null)
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(bindMountPath)!, recursive: true);
                }
                catch
                {
                    // Don't fail test if we can't clean the temporary folder
                }
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task VerifyWaitForOnValkeyBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddValkey("resource")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddValkey("dependentresource")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }
}
