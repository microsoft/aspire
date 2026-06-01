// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Net;
using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Polly;

namespace Aspire.Hosting.Redis.Tests;

public class RedisFunctionalTests(ITestOutputHelper testOutputHelper)
{
    private const UnixFileMode MountFilePermissions =
       UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
       UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnRedisBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddRedis("resource")
                           .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddRedis("dependentresource")
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
    public async Task VerifyRedisCommanderResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        IResourceBuilder<RedisCommanderResource>? commanderBuilder = null;
        var redis = builder.AddRedis("redis").WithRedisCommander(c => commanderBuilder = c);
        Assert.NotNull(commanderBuilder);

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Redis Connection", resourceName: commanderBuilder.Resource.Name);

        var client = app.CreateHttpClient(commanderBuilder.Resource.Name, "http");

        var endpoint = redis.GetEndpoint(RedisResource.PrimaryEndpointName);
        if (redis.Resource.TlsEnabled)
        {
            // We don't support TLS connections in Redis Commander yet, so we need to use the secondary endpoint which is non-TLS
            endpoint = redis.GetEndpoint(RedisResource.SecondaryEndpointName);
        }

        var path = $"/apiv2/server/R:{redis.Resource.Name}:{endpoint.TargetPort}:0/info";
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyRedisResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var redis = builder.AddRedis("redis");

        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{redis.Resource.Name}"] = await redis.Resource.GetConnectionStringAsync()
        });

        hb.AddRedisClient(redis.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

        var db = redisClient.GetDatabase();

        await db.StringSetAsync("key", "value");

        var value = await db.StringGetAsync("key");

        Assert.Equal("value", value);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithRedisInsightImportDatabases()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var redis1 = builder.AddRedis("redis-1");
        IResourceBuilder<RedisInsightResource>? redisInsightBuilder = null;
        var redis2 = builder.AddRedis("redis-2").WithRedisInsight(c => redisInsightBuilder = c);
        Assert.NotNull(redisInsightBuilder);
        var redis3 = builder.AddRedis("redis-3").WithPassword(null);

        using var app = builder.Build();

        await app.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(redisInsightBuilder.Resource.Name, KnownResourceStates.Running).WaitAsync(cts.Token);

        var client = app.CreateHttpClient(redisInsightBuilder.Resource.Name, "http");

        // Accept EULA first; otherwise, /api/databases will not work properly.
        await AcceptRedisInsightEula(client, cts.Token);

        var response = await client.GetAsync("/api/databases", cts.Token);
        response.EnsureSuccessStatusCode();

        var databases = await response.Content.ReadFromJsonAsync<List<RedisInsightDatabaseModel>>(cts.Token);
        Assert.NotNull(databases);

        databases = [.. databases.OrderBy(d => d.Id)];
        Assert.NotNull(databases);
        Assert.Collection(databases,
        db =>
        {
            Assert.Equal(redis1.Resource.Name, db.Name);
            Assert.Equal($"{redis1.Resource.Name}.dev.internal", db.Host);
            Assert.Equal(redis1.Resource.PrimaryEndpoint.TargetPort, db.Port);
        },
        db =>
        {
            Assert.Equal(redis2.Resource.Name, db.Name);
            Assert.Equal($"{redis2.Resource.Name}.dev.internal", db.Host);
            Assert.Equal(redis2.Resource.PrimaryEndpoint.TargetPort, db.Port);
        },
        db =>
        {
            Assert.Equal(redis3.Resource.Name, db.Name);
            Assert.Equal($"{redis3.Resource.Name}.dev.internal", db.Host);
            Assert.Equal(redis3.Resource.PrimaryEndpoint.TargetPort, db.Port);
        });

        foreach (var db in databases)
        {
            var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var testConnectionResponse = await client.GetAsync($"/api/databases/{db.Id}/connect", cts2.Token);
            testConnectionResponse.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataVolumeShouldPersistStateBetweenUsages()
    {
        // Use a volume to do a snapshot save

        using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var redis1 = builder1.AddRedis("redis");

        // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
        var volumeName = VolumeNameGenerator.Generate(redis1, nameof(WithDataVolumeShouldPersistStateBetweenUsages));
        redis1.WithDataVolume(volumeName);
        // if the volume already exists (because of a crashing previous run), delete it
        DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);

        using (var app = builder1.Build())
        {
            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();
            hb.AddTestLogging(testOutputHelper);

            // BGSAVE is only available in admin mode, enable it for this instance
            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{redis1.Resource.Name}"] = $"{await redis1.Resource.GetConnectionStringAsync()},allowAdmin=true"
            });

            hb.AddRedisClient(redis1.Resource.Name);

            using (var host = hb.Build())
            {
                await host.StartAsync();

                var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                var db = redisClient.GetDatabase();

                await db.StringSetAsync("key", "value");

                // Force Redis to save the keys (snapshotting)
                // c.f. https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/

                await redisClient.GetServers().First().SaveAsync(SaveType.BackgroundSave);
            }

            // Stops the container, or the Volume would still be in use
            await app.StopAsync();
        }

        using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var redis2 = builder2.AddRedis("redis").WithDataVolume(volumeName);

        using (var app = builder2.Build())
        {
            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();
            hb.AddTestLogging(testOutputHelper);

            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{redis2.Resource.Name}"] = await redis2.Resource.GetConnectionStringAsync()
            });

            hb.AddRedisClient(redis2.Resource.Name);

            using (var host = hb.Build())
            {
                await host.StartAsync();

                var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                var db = redisClient.GetDatabase();

                var value = await db.StringGetAsync("key");

                Assert.Equal("value", value);
            }

            // Stops the container, or the Volume would still be in use
            await app.StopAsync();
        }

        DockerUtils.AttemptDeleteDockerVolume(volumeName);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataBindMountShouldPersistStateBetweenUsages()
    {
        var bindMountPath = Directory.CreateTempSubdirectory().FullName;

        // Use a bind mount to do a snapshot save

        using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var redis1 = builder1.AddRedis("redis").WithDataBindMount(bindMountPath);

        using (var app = builder1.Build())
        {
            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();
            hb.AddTestLogging(testOutputHelper);

            // BGSAVE is only available in admin mode, enable it for this instance
            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{redis1.Resource.Name}"] = $"{await redis1.Resource.GetConnectionStringAsync()},allowAdmin=true"
            });

            hb.AddRedisClient(redis1.Resource.Name);

            using (var host = hb.Build())
            {
                await host.StartAsync();

                var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                var db = redisClient.GetDatabase();

                await db.StringSetAsync("key", "value");

                // Force Redis to save the keys (snapshotting)
                // c.f. https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/

                await redisClient.GetServers().First().SaveAsync(SaveType.BackgroundSave);
            }

            await app.StopAsync();
        }

        using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var redis2 = builder2.AddRedis("redis").WithDataBindMount(bindMountPath);

        using (var app = builder2.Build())
        {
            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{redis2.Resource.Name}"] = await redis2.Resource.GetConnectionStringAsync()
            });

            hb.AddRedisClient(redis2.Resource.Name);

            using (var host = hb.Build())
            {
                await host.StartAsync();

                var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                var db = redisClient.GetDatabase();

                var value = await db.StringGetAsync("key");

                Assert.Equal("value", value);
            }

            await app.StopAsync();
        }

        try
        {
            Directory.Delete(bindMountPath, recursive: true);
        }
        catch
        {
            // Don't fail test if we can't clean the temporary folder
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task PersistenceIsDisabledByDefault()
    {
        // Checks that without enabling Redis Persistence the tests fail

        using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var redis1 = builder1.AddRedis("redis");

        using (var app = builder1.Build())
        {
            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();
            hb.AddTestLogging(testOutputHelper);

            // BGSAVE is only available in admin mode, enable it for this instance
            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{redis1.Resource.Name}"] = $"{await redis1.Resource.GetConnectionStringAsync()},allowAdmin=true"
            });

            hb.AddRedisClient(redis1.Resource.Name);

            using (var host = hb.Build())
            {
                await host.StartAsync();

                var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                var db = redisClient.GetDatabase();

                await db.StringSetAsync("key", "value");
            }

            await app.StopAsync();
        }

        using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var redis2 = builder2.AddRedis("redis");

        using (var app = builder2.Build())
        {
            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();
            hb.AddTestLogging(testOutputHelper);

            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{redis2.Resource.Name}"] = await redis2.Resource.GetConnectionStringAsync()
            });

            hb.AddRedisClient(redis2.Resource.Name);

            using (var host = hb.Build())
            {
                await host.StartAsync();

                var redisClient = host.Services.GetRequiredService<IConnectionMultiplexer>();

                var db = redisClient.GetDatabase();

                var value = await db.StringGetAsync("key");

                Assert.True(value.IsNull);
            }

            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task RedisInsightWithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            IResourceBuilder<RedisInsightResource>? redisInsightBuilder1 = null;
            var redis1 = builder1.AddRedis("redis")
                .WithRedisInsight(c => { redisInsightBuilder1 = c; });
            Assert.NotNull(redisInsightBuilder1);

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(redisInsightBuilder1, nameof(RedisInsightWithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                redisInsightBuilder1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Directory.CreateTempSubdirectory().FullName;

                if (!OperatingSystem.IsWindows())
                {
                    // Change permissions for non-root accounts (container user account)
                    File.SetUnixFileMode(bindMountPath, MountFilePermissions);
                }

                redisInsightBuilder1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();

                var rns = app.Services.GetRequiredService<ResourceNotificationService>();
                await rns.WaitForResourceAsync(redisInsightBuilder1.Resource.Name, KnownResourceStates.Running).WaitAsync(cts.Token);

                try
                {
                    var httpClient = app.CreateHttpClient(redisInsightBuilder1.Resource.Name, "http");
                    await AcceptRedisInsightEula(httpClient, cts.Token);
                }
                finally
                {
                    // Stops the container, or the Volume would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

            IResourceBuilder<RedisInsightResource>? redisInsightBuilder2 = null;
            var redis2 = builder2.AddRedis("redis")
                .WithRedisInsight(c => { redisInsightBuilder2 = c; });
            Assert.NotNull(redisInsightBuilder2);

            if (useVolume)
            {
                redisInsightBuilder2.WithDataVolume(volumeName);
            }
            else
            {
                redisInsightBuilder2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();

                var rns = app.Services.GetRequiredService<ResourceNotificationService>();
                await rns.WaitForResourceAsync(redisInsightBuilder2.Resource.Name, KnownResourceStates.Running).WaitAsync(cts.Token);

                try
                {
                    var httpClient = app.CreateHttpClient(redisInsightBuilder2.Resource.Name, "http");
                    await EnsureRedisInsightEulaAccepted(httpClient, cts.Token);
                }
                finally
                {
                    // Stops the container, or the Volume would still be in use
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

    private static async Task EnsureRedisInsightEulaAccepted(HttpClient httpClient, CancellationToken ct)
    {
        var pipeline = new ResiliencePipelineBuilder()
                            .AddRetry(new() { MaxRetryAttempts = 10, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
                            .Build();

        await pipeline.ExecuteAsync(async ct =>
        {
            var response = await httpClient.GetAsync("/api/settings", ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);

            var jo = JsonNode.Parse(content);
            Assert.NotNull(jo);
            var agreements = jo["agreements"];

            Assert.NotNull(agreements);
            Assert.False(agreements["analytics"]!.GetValue<bool>());
            Assert.False(agreements["notifications"]!.GetValue<bool>());
            Assert.False(agreements["encryption"]!.GetValue<bool>());
            Assert.True(agreements["eula"]!.GetValue<bool>());
        }, ct);
    }

    static async Task AcceptRedisInsightEula(HttpClient client, CancellationToken ct)
    {
        var jsonContent = JsonContent.Create(new
        {
            agreements = new
            {
                eula = true,
                analytics = false,
                notifications = false,
                encryption = false,
            }
        });

        var apiUrl = $"/api/settings";

        var response = await client.PatchAsync(apiUrl, jsonContent, ct);

        response.EnsureSuccessStatusCode();

        await EnsureRedisInsightEulaAccepted(client, ct);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithRedisCommanderShouldWorkWithPassword()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var passwordParameter = builder.AddParameter("pass", "p@ssw0rd1");

        var redis = builder.AddRedis("redis", password: passwordParameter)
           .WithRedisCommander();

        builder.Services.AddHttpClient();
        using var app = builder.Build();

        await app.StartAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var redisCommander = Assert.Single(appModel.Resources.OfType<RedisCommanderResource>());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(redis.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(redisCommander.Name, cts.Token);

        var endpoint = redisCommander.GetEndpoint("http");
        var redisCommanderUrl = endpoint.Url;
        Assert.NotNull(redisCommanderUrl);

        var clientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
        var client = clientFactory.CreateClient();

        var httpResponse = await client.GetAsync(redisCommanderUrl!);
        httpResponse.EnsureSuccessStatusCode();
    }

    internal sealed class RedisInsightDatabaseModel
    {
        public string? Id { get; set; }
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? Name { get; set; }
    }
}
