// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Polly;
using StackExchange.Redis;

namespace Aspire.Hosting.Garnet.Tests;

public class GarnetFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnGarnetBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddGarnet("resource")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddGarnet("dependentresource")
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

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyGarnetResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
           .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(3) })
           .Build();

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var garnet = builder.AddGarnet("garnet");

        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{garnet.Resource.Name}"] = await garnet.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddRedisClient(garnet.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

        await pipeline.ExecuteAsync(async token =>
         {
             var db = redisClient.GetDatabase();

             await db.StringSetAsync("key", "value");

             var value = await db.StringGetAsync("key");

             Assert.Equal("value", value);

         }, cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
                            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(10) })
                            .Build();
        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            var builder1 = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
            var garnet1 = builder1.AddGarnet("garnet");

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(garnet1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), try to delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName);
                garnet1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Directory.CreateTempSubdirectory().FullName;

                if (!OperatingSystem.IsWindows())
                {
                    // The docker container runs as a non-root user, so we need to grant other user's read/write permission
                    // to the bind mount directory.
                    // Note that we need to do this after creating the directory, because the umask is applied at the time of creation.
                    const UnixFileMode BindMountPermissions =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

                    File.SetUnixFileMode(bindMountPath, BindMountPermissions);
                }

                garnet1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{garnet1.Resource.Name}"] = $"{await garnet1.Resource.ConnectionStringExpression.GetValueAsync(default)}";

                    hb.AddRedisClient("garnet");

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                            var db = redisClient.GetDatabase();

                            await db.StringSetAsync("key", "value");
                            var value = await db.StringGetAsync("key");

                            // Force Garnet to save the keys
                            // c.f. https://microsoft.github.io/garnet/docs/commands/checkpoint#save
                            await db.ExecuteAsync("SAVE");

                            Assert.Equal("value", value);
                        }, cts.Token);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            var builder2 = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
            var garnet2 = builder2.AddGarnet("garnet");

            if (useVolume)
            {
                garnet2.WithDataVolume(volumeName);
            }
            else
            {
                garnet2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{garnet2.Resource.Name}"] = $"{await garnet2.Resource.ConnectionStringExpression.GetValueAsync(default)}";

                    hb.AddRedisClient("garnet");

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                            var db = redisClient.GetDatabase();

                            var value = await db.StringGetAsync("key");

                            Assert.Equal("value", value);
                        });
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
                    Directory.Delete(bindMountPath, recursive: true);
                }
                catch
                {
                    // Don't fail test if we can't clean the temporary folder
                }
            }
        }
    }
}
