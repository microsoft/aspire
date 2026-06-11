// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Polly;

namespace Aspire.Hosting.Azure.Tests;

public class AzureCosmosDBEmulatorFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    // [InlineData(true)] // "Using CosmosDB emulator in integration tests leads to flaky tests - https://github.com/microsoft/aspire/issues/5820"
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnCosmosDBEmulatorBlocksDependentResources(bool usePreview)
    {
        // Cosmos can be pretty slow to spin up, lets give it plenty of time.
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddAzureCosmosDB("resource")
                              .RunAsEmulator(usePreview)
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
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

    [Theory(Skip = "Using CosmosDB emulator in integration tests leads to flaky tests - https://github.com/microsoft/aspire/issues/5820")]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyCosmosResource(bool usePreview)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = 10,
                Delay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Linear,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
            })
            .Build();

        // @sebastienros: we won't use netaspireci.azurecr.io since the image is on mcr.microsoft.com, so Create is the way to go
        using var builder = TestDistributedApplicationBuilder.Create(options => { }, testOutputHelper);

        var databaseName = "db1";
        var containerName = "container1";

        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .RunAsEmulator(usePreview);
        var db = cosmos.AddCosmosDatabase(databaseName);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(db.Resource.Name, cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);
        hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await cosmos.Resource.ConnectionStringExpression.GetValueAsync(default);
        hb.AddAzureCosmosClient(db.Resource.Name);
        hb.AddCosmosDbContext<EFCoreCosmosDbContext>(db.Resource.Name, databaseName);

        using var host = hb.Build();

        await host.StartAsync();

        // This needs to be outside the pipeline because when the CosmosClient is disposed,
        // there is an exception in the pipeline
        using var cosmosClient = host.Services.GetRequiredService<CosmosClient>();
        using var dbContext = host.Services.GetRequiredService<EFCoreCosmosDbContext>();

        await pipeline.ExecuteAsync(async token =>
        {
            Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: token);
            Container container = await database.CreateContainerIfNotExistsAsync(containerName, "/id", cancellationToken: token);

            var testObject = new { id = "1", data = "assertionValue" };
            await container.CreateItemAsync(testObject, cancellationToken: token);

            // run query and check the value
            QueryDefinition query = new("SELECT VALUE c.data FROM c WHERE c.id = '1'");
            var results = await container.GetItemQueryIterator<string>(query).ReadNextAsync(token);

            Assert.True(results.Count == 1);
            Assert.True(results.First() == testObject.data);

            await dbContext.Database.EnsureCreatedAsync(token);
            dbContext.AddRange([new Entry(), new Entry()]);
            var count = await dbContext.SaveChangesAsync(token);
            Assert.Equal(2, count);
        }, cts.Token);
    }

    [Theory(Skip = "Using CosmosDB emulator in integration tests leads to flaky tests - https://github.com/microsoft/aspire/issues/5820")]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataVolumeShouldPersistStateBetweenUsages(bool usePreview)
    {
        // Use a volume to do a snapshot save

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = 10,
                Delay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Linear,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
            })
            .Build();

        var databaseName = "db";
        var containerName = "container";

        using var builder1 = TestDistributedApplicationBuilder.Create(options => { }, testOutputHelper);
        var cosmos1 = builder1.AddAzureCosmosDB("cosmos");

        // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
        var volumeName = VolumeNameGenerator.Generate(cosmos1, nameof(WithDataVolumeShouldPersistStateBetweenUsages));

        cosmos1.RunAsEmulator(usePreview, volumeName);

        cosmos1.AddCosmosDatabase(databaseName);

        // if the volume already exists (because of a crashing previous run), delete it
        DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);

        var testObject = new { id = "1", data = "assertionValue" };

        using (var app = builder1.Build())
        {
            await app.StartAsync(cts.Token);

            var rns = app.Services.GetRequiredService<ResourceNotificationService>();
            await rns.WaitForResourceHealthyAsync(cosmos1.Resource.Name, cts.Token);

            try
            {
                var hb = Host.CreateApplicationBuilder();

                hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{cosmos1.Resource.Name}"] = await cosmos1.Resource.ConnectionStringExpression.GetValueAsync(default)
                });

                hb.AddAzureCosmosClient(cosmos1.Resource.Name);

                using (var host = hb.Build())
                {
                    await host.StartAsync();

                    // This needs to be outside the pipeline because when the CosmosClient is disposed,
                    // there is an exception in the pipeline
                    using var cosmosClient = host.Services.GetRequiredService<CosmosClient>();

                    await pipeline.ExecuteAsync(async token =>
                    {
                        Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: token);
                        Container container = await database.CreateContainerIfNotExistsAsync(containerName, "/id", cancellationToken: token);

                        await container.CreateItemAsync(testObject, cancellationToken: token);
                    }, cts.Token);
                }
            }
            finally
            {
                // Stops the container, or the Volume/mount would still be in use
                await app.StopAsync();
            }
        }

        using var builder2 = TestDistributedApplicationBuilder.Create(options => { }, testOutputHelper);

        var cosmos2 = builder2.AddAzureCosmosDB("cosmos")
            .RunAsEmulator(usePreview, volumeName);
        cosmos2.AddCosmosDatabase(databaseName);

        using (var app = builder2.Build())
        {
            await app.StartAsync(cts.Token);

            var rns = app.Services.GetRequiredService<ResourceNotificationService>();
            await rns.WaitForResourceHealthyAsync(cosmos2.Resource.Name, cts.Token);

            try
            {
                var hb = Host.CreateApplicationBuilder();
                hb.AddTestLogging(testOutputHelper);

                hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{cosmos2.Resource.Name}"] = await cosmos2.Resource.ConnectionStringExpression.GetValueAsync(default)
                });

                hb.AddAzureCosmosClient(cosmos2.Resource.Name);

                using (var host = hb.Build())
                {
                    await host.StartAsync();

                    // This needs to be outside the pipeline because when the CosmosClient is disposed,
                    // there is an exception in the pipeline
                    using var cosmosClient = host.Services.GetRequiredService<CosmosClient>();

                    await pipeline.ExecuteAsync(async token =>
                    {
                        var container = cosmosClient.GetContainer(databaseName, containerName);

                        QueryDefinition query = new("SELECT VALUE c.data FROM c WHERE c.id = '1'");

                        // run query and check the value
                        var results = await container.GetItemQueryIterator<string>(query).ReadNextAsync(token);

                        Assert.True(results.Count == 1);
                        Assert.True(results.First() == testObject.data);

                    }, cts.Token);
                }
            }
            finally
            {
                // Stops the container, or the Volume/mount would still be in use
                await app.StopAsync();
            }
        }

        DockerUtils.AttemptDeleteDockerVolume(volumeName);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/7178")]
    public async Task AddAzureCosmosDB_RunAsEmulator_CreatesDatabase()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        using var builder = TestDistributedApplicationBuilder.Create(options => { }, testOutputHelper);

        var databaseName = "db1";
        var containerName = "container1";
        var partitionKeyPath = "/id";

        var cosmos = builder.AddAzureCosmosDB("cosmos")
                            .RunAsEmulator();
        var db = cosmos.AddCosmosDatabase(databaseName);
        db.AddContainer(containerName, partitionKeyPath);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(cosmos.Resource.Name, cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);
        hb.Configuration[$"ConnectionStrings:{cosmos.Resource.Name}"] = await cosmos.Resource.ConnectionStringExpression.GetValueAsync(default);
        hb.AddAzureCosmosClient(cosmos.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync(cts.Token);

        using var cosmosClient = host.Services.GetRequiredService<CosmosClient>();

        var database = cosmosClient.GetDatabase(databaseName);
        var result1 = await database.ReadAsync(cancellationToken: cts.Token);

        var container = database.GetContainer(containerName);
        var result2 = await container.ReadContainerAsync(cancellationToken: cts.Token);

        Assert.True(IsSuccess(result1.StatusCode));
        Assert.True(IsSuccess(result2.StatusCode));

        static bool IsSuccess(HttpStatusCode httpStatusCode)
        {
            return ((int)httpStatusCode >= 200) && ((int)httpStatusCode <= 299);
        }
    }
}

public class EFCoreCosmosDbContext(DbContextOptions<EFCoreCosmosDbContext> options) : DbContext(options)
{
    public DbSet<Entry> Entries { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entry>()
            .HasPartitionKey(e => e.Id);
    }
}

public record Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

internal static class CosmosExtensions
{
    public static IResourceBuilder<AzureCosmosDBResource> RunAsEmulator(this IResourceBuilder<AzureCosmosDBResource> builder, bool usePreview, string? volumeName = null)
    {
        void WithVolume(IResourceBuilder<AzureCosmosDBEmulatorResource> emulator)
        {
            if (volumeName is not null)
            {
                emulator.WithDataVolume(volumeName);
            }
        }

        return usePreview
#pragma warning disable ASPIRECOSMOSDB001 // RunAsPreviewEmulator is experimental
            ? builder.RunAsPreviewEmulator(WithVolume)
#pragma warning restore ASPIRECOSMOSDB001 // RunAsPreviewEmulator is experimental
            : builder.RunAsEmulator(WithVolume);
    }
}
