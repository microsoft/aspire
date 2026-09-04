// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable ASPIRECERTIFICATES001 // Certificate APIs are experimental.
#pragma warning disable ASPIREMONGODB001 // MongoDB replica set APIs are experimental.

using Aspire.TestUtilities;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;
using Polly;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.MongoDB.Tests;

public class MongoDbFunctionalTests(ITestOutputHelper testOutputHelper)
{
    private const string CollectionName = "movie_collection";

    private static readonly Movie[] s_movies =
        [
            new() { Name = "The Shawshank Redemption"},
            new() { Name = "The Godfather"},
            new() { Name = "The Dark Knight"},
            new() { Name = "Schindler's List"},
        ];

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task VerifyWaitForOnMongoBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddMongoDB("resource")
                           .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddMongoDB("dependentresource")
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
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task VerifyMongoDBResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var mongodb = builder.AddMongoDB("mongodb");
        var db = mongodb.AddDatabase("testdb");
        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddMongoDBClient(db.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await pipeline.ExecuteAsync(async token =>
        {
            var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();

            await CreateTestDataAsync(mongoDatabase, token);
        }, cts.Token);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresFeature(TestFeature.DevCert)]
    public async Task VerifyMongoExpressConnectsWhenMongoDBUsesTls()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        // NOTE: The retry budget has to cover Mongo Express starting up, which happens after its container is already
        // reported as running, and which is not fast on a cold agent.
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 30, Delay = TimeSpan.FromSeconds(3) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // NOTE: Mongo Express speaks plain TCP unless it is told otherwise, so this is what catches a MongoDB server that
        // serves TLS while its companion admin UI has not been configured for it.
        var mongoExpress = null as IResourceBuilder<MongoExpressContainerResource>;
        var mongodb = builder.AddMongoDB("mongodb")
            .WithHttpsDeveloperCertificate()
            .WithMongoExpress(configureContainer: c => mongoExpress = c);

        Assert.NotNull(mongoExpress);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongodb.Resource.Name, cts.Token);
        Assert.True(mongodb.Resource.TlsEnabled);

        await app.ResourceNotifications.WaitForResourceAsync(mongoExpress.Resource.Name, KnownResourceStates.Running, cts.Token);

        var endpoint = mongoExpress.Resource.GetEndpoint("http");
        using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

        // NOTE: Mongo Express connects to MongoDB while it starts up and gives up if it cannot, so being able to serve its
        // database listing is what proves the TLS connection was actually established.
        await pipeline.ExecuteAsync(async token =>
        {
            using var response = await httpClient.GetAsync("/", token);
            response.EnsureSuccessStatusCode();
        }, cts.Token);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresFeature(TestFeature.DevCert)]
    public async Task VerifyMongoDBWithTls()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // NOTE: MongoDB serves TLS whenever a certificate is available for it; opting in to the developer certificate
        // explicitly makes the test independent of whether that is also the ambient default on this machine.
        var mongodb = builder.AddMongoDB("mongodb")
            .WithHttpsDeveloperCertificate();
        var db = mongodb.AddDatabase("testdb");
        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        var connectionString = await db.Resource.ConnectionStringExpression.GetValueAsync(default);
        hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = connectionString;
        Assert.Contains("tls=true", connectionString, StringComparison.OrdinalIgnoreCase);

        hb.AddMongoDBClient(db.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await pipeline.ExecuteAsync(async token =>
        {
            var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();
            await CreateTestDataAsync(mongoDatabase, token);
        }, cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var dbName = "testdb";
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var mongodb1 = builder1.AddMongoDB("mongodb");
#pragma warning disable CS0618 // Type or member is obsolete
            var password = mongodb1.Resource.PasswordParameter!.Value;
#pragma warning restore CS0618 // Type or member is obsolete
            var db1 = mongodb1.AddDatabase(dbName);

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(mongodb1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                mongodb1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "data");

                mongodb1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{db1.Resource.Name}"] = await db1.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddMongoDBClient(db1.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();
                            await CreateTestDataAsync(mongoDatabase, token);
                        }, cts.Token);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var passwordParameter2 = builder2.AddParameter("pwd", password);

            var mongodb2 = builder2.AddMongoDB("mongodb", password: passwordParameter2);
            var db2 = mongodb2.AddDatabase(dbName);

            if (useVolume)
            {
                mongodb2.WithDataVolume(volumeName);
            }
            else
            {
                mongodb2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{db2.Resource.Name}"] = await db2.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddMongoDBClient(db2.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();

                            var collection = mongoDatabase.GetCollection<Movie>(CollectionName);

                            var results = await collection.Find(new BsonDocument()).ToListAsync(token);

                            Assert.Collection(results,
                                            item => Assert.Contains("The Shawshank Redemption", item.Name),
                                            item => Assert.Contains("The Godfather", item.Name),
                                            item => Assert.Contains("The Dark Knight", item.Name),
                                            item => Assert.Contains("Schindler's List", item.Name)
                                            );
                        }, cts.Token);
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
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task VerifyWithInitBindMount()
    {
        // Creates a script that should be executed when the container is initialized.

        var dbName = "testdb";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        var bindMountPath = Directory.CreateTempSubdirectory().FullName;

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode BindMountPermissions =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

            File.SetUnixFileMode(bindMountPath, BindMountPermissions);
        }

        try
        {
            var initFilePath = Path.Combine(bindMountPath, "mongo-init.js");
            await File.WriteAllTextAsync(initFilePath, $$"""
                db = db.getSiblingDB('{{dbName}}');

                db.createCollection('{{CollectionName}}');

                db.{{CollectionName}}.insertMany([
                    {
                        name: 'The Shawshank Redemption'
                    },
                    {
                        name: 'The Godfather'
                    },
                    {
                        name: 'The Dark Knight'
                    },
                    {
                        name: 'Schindler\'s List'
                    }
                ]);
            """);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(initFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

#pragma warning disable CS0618 // Type or member is obsolete
            var mongodb = builder.AddMongoDB("mongodb")
                .WithInitBindMount(bindMountPath);
#pragma warning restore CS0618 // Type or member is obsolete

            var db = mongodb.AddDatabase(dbName);
            using var app = builder.Build();

            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

            hb.AddMongoDBClient(db.Resource.Name);

            using var host = hb.Build();

            await host.StartAsync();

            var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();

            await pipeline.ExecuteAsync(async token =>
            {
                var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();

                var collection = mongoDatabase.GetCollection<Movie>(CollectionName);

                var results = await collection.Find(new BsonDocument()).ToListAsync(token);

                Assert.Collection(results,
                                item => Assert.Contains("The Shawshank Redemption", item.Name),
                                item => Assert.Contains("The Godfather", item.Name),
                                item => Assert.Contains("The Dark Knight", item.Name),
                                item => Assert.Contains("Schindler's List", item.Name)
                                );
            }, cts.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(bindMountPath);
            }
            catch
            {
                // Don't fail test if we can't clean the temporary folder
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task VerifyWithInitFiles()
    {
        // Creates a script that should be executed when the container is initialized.

        var dbName = "testdb";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        var initFilesPath = Directory.CreateTempSubdirectory().FullName;

        try
        {
            var initFilePath = Path.Combine(initFilesPath, "mongo-init.js");
            await File.WriteAllTextAsync(initFilePath, $$"""
                db = db.getSiblingDB('{{dbName}}');

                db.createCollection('{{CollectionName}}');

                db.{{CollectionName}}.insertMany([
                    {
                        name: 'The Shawshank Redemption'
                    },
                    {
                        name: 'The Godfather'
                    },
                    {
                        name: 'The Dark Knight'
                    },
                    {
                        name: 'Schindler\'s List'
                    }
                ]);
            """);

            using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

            var mongodb = builder.AddMongoDB("mongodb")
                .WithInitFiles(initFilesPath);

            var db = mongodb.AddDatabase(dbName);
            using var app = builder.Build();

            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

            hb.AddMongoDBClient(db.Resource.Name);

            using var host = hb.Build();

            await host.StartAsync();

            var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();

            await pipeline.ExecuteAsync(async token =>
            {
                var mongoDatabase = host.Services.GetRequiredService<IMongoDatabase>();

                var collection = mongoDatabase.GetCollection<Movie>(CollectionName);

                var results = await collection.Find(new BsonDocument()).ToListAsync(token);

                Assert.Collection(results,
                                item => Assert.Contains("The Shawshank Redemption", item.Name),
                                item => Assert.Contains("The Godfather", item.Name),
                                item => Assert.Contains("The Dark Knight", item.Name),
                                item => Assert.Contains("Schindler's List", item.Name)
                                );
            }, cts.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(initFilesPath);
            }
            catch
            {
                // Don't fail test if we can't clean the temporary folder
            }
        }
    }

    private static async Task CreateTestDataAsync(IMongoDatabase mongoDatabase, CancellationToken token)
    {
        await mongoDatabase.CreateCollectionAsync(CollectionName, cancellationToken: token);
        var collection = mongoDatabase.GetCollection<Movie>(CollectionName);
        await collection.InsertManyAsync(s_movies, cancellationToken: token);

        var results = await collection.Find(new BsonDocument()).ToListAsync(token);

        Assert.Collection(results,
                        item => Assert.Contains("The Shawshank Redemption", item.Name),
                        item => Assert.Contains("The Godfather", item.Name),
                        item => Assert.Contains("The Dark Knight", item.Name),
                        item => Assert.Contains("Schindler's List", item.Name)
                        );
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public Task MongoDB_WithPersistentLifetime_ReusesContainer()
    {
        return PersistentContainerTestHelpers.AssertResourceReusesContainerAsync(
            testOutputHelper,
            builder => builder.AddMongoDB("resource").WithPersistentLifetime(),
            "resource",
            useTestContainerRegistry: true);
    }
}

public class Movie
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("name")]
    public string? Name { get; set; }
}

public class Director
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("name")]
    public string? Name { get; set; }
}
