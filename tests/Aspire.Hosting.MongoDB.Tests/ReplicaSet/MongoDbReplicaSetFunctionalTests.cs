// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Utils;
using MongoDB.Bson;
using MongoDB.Driver;
using Polly;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.MongoDB.Tests;

public class MongoDbReplicaSetFunctionalTests(ITestOutputHelper testOutputHelper)
{
    private const string DbName = "testdb";
    private const string CollectionNameA = "movie_collection";
    private const string CollectionNameB = "directors_collection";

    private static readonly Movie[] s_movies =
    [
        new() { Name = "The Shawshank Redemption"},
        new() { Name = "The Godfather"},
        new() { Name = "The Dark Knight"},
        new() { Name = "Schindler's List"},
    ];
    private static readonly Director[] s_directors =
    [
        new() { Name = "Quentin Tarantino"},
        new() { Name = "Francis Ford Coppola"},
        new() { Name = "Christopher Nolan"},
        new() { Name = "Steven Spielberg"},
    ];

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMongoDBReplicaSetResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var mongo = builder.AddMongoDB("mongo1");
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

        var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

        await pipeline.ExecuteAsync(async token =>
        {
            var client = new MongoClient(connectionString);
            var db = client.GetDatabase(DbName);
            await CreateTestDataWithReplicaSetFeaturesAsync(db, cts.Token);
        }, cts.Token);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMongoDBMultiNodeReplicaSetResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var mongo1 = builder.AddMongoDB("mongo1");
        var mongo2 = builder.AddMongoDB("mongo2");
        var mongo3 = builder.AddMongoDB("mongo3");
        var rs = builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1)
            .WithMember(mongo2)
            .WithMember(mongo3);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

        var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

        await pipeline.ExecuteAsync(async token =>
        {
            var client = new MongoClient(connectionString);
            var db = client.GetDatabase(DbName);
            await CreateTestDataWithReplicaSetFeaturesAsync(db, cts.Token);
        }, cts.Token);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMongoDBMultiNodeReplicaSetAllNodesEndUpHealthy()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var mongo1 = builder.AddMongoDB("mongo1");
        var mongo2 = builder.AddMongoDB("mongo2");
        var mongo3 = builder.AddMongoDB("mongo3");
        var rs = builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1)
            .WithMember(mongo2)
            .WithMember(mongo3);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo1.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo2.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo3.Resource.Name, cts.Token);

        await app.StopAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages(bool changeTopology)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var volumeName1 = null as string;
        var volumeName2 = null as string;
        var volumeName3 = null as string;
        var volumeName4 = null as string;
        try
        {
            var password = null as string;
            using (var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper))
            {
                var mongo1 = builder.AddMongoDB("mongo1");
                volumeName1 = VolumeNameGenerator.Generate(mongo1, nameof(VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages));
                mongo1 = mongo1.WithDataVolume(volumeName1);

                var mongo2 = builder.AddMongoDB("mongo2");
                volumeName2 = VolumeNameGenerator.Generate(mongo2, nameof(VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages));
                mongo2 = mongo2.WithDataVolume(volumeName2);

                var mongo3 = builder.AddMongoDB("mongo3");
                volumeName3 = VolumeNameGenerator.Generate(mongo3, nameof(VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages));
                mongo3 = mongo3.WithDataVolume(volumeName3);

                // NOTE: If the volumes already exist (because of a crashing previous run), delete them.
                DockerUtils.AttemptDeleteDockerVolume(volumeName1);
                DockerUtils.AttemptDeleteDockerVolume(volumeName2);
                DockerUtils.AttemptDeleteDockerVolume(volumeName3);

                var rs = builder.AddMongoDBReplicaSet("rs0")
                    .WithMember(mongo1)
                    .WithMember(mongo2)
                    .WithMember(mongo3);

                password = await rs.Resource.SharedPasswordParameter.GetValueAsync(cts.Token);
                using var app = builder.Build();
                await app.StartAsync(cts.Token);

                await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

                var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);
                var client = new MongoClient(connectionString);
                var db = client.GetDatabase(DbName);
                await CreateTestDataWithReplicaSetFeaturesAsync(db, cts.Token);

                await app.StopAsync();
            }

            using (var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper))
            {
                var passwordParameter = builder.AddParameter("mongoPassword", value: password!);
                var mongo1 = builder.AddMongoDB("mongo1");
                mongo1 = mongo1.WithDataVolume(volumeName1);

                var mongo2 = builder.AddMongoDB("mongo2");
                mongo2 = mongo2.WithDataVolume(volumeName2);

                var mongo3 = builder.AddMongoDB("mongo3");
                mongo3 = mongo3.WithDataVolume(volumeName3);

                var rs = builder.AddMongoDBReplicaSet("rs0", password: passwordParameter)
                    .WithMember(mongo1)
                    .WithMember(mongo2)
                    .WithMember(mongo3);

                // NOTE: We add a new node to the replica set.
                if (changeTopology)
                {
                    var mongo4 = builder.AddMongoDB("mongo4");
                    volumeName4 = VolumeNameGenerator.Generate(mongo4, nameof(VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages));
                    // NOTE: If the volume already exists (because of a crashing previous run), delete it.
                    DockerUtils.AttemptDeleteDockerVolume(volumeName4);
                    rs = rs.WithMember(mongo4);
                }

                using var app = builder.Build();
                await app.StartAsync(cts.Token);

                await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

                var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);
                var client = new MongoClient(connectionString);
                var db = client.GetDatabase(DbName);
                var moviesCollection = db.GetCollection<Movie>(CollectionNameA);
                var data = await moviesCollection.Find(_ => true).SortBy(e => e.Name).ToListAsync(cts.Token);
                Assert.Collection(data,
                    item => Assert.Equal("Schindler's List", item.Name),
                    item => Assert.Equal("The Dark Knight", item.Name),
                    item => Assert.Equal("The Godfather", item.Name),
                    item => Assert.Equal("The Shawshank Redemption", item.Name)
                );

                await app.StopAsync();
            }
        }
        finally
        {
            if (volumeName1 is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName1);
            }
            if (volumeName2 is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName2);
            }
            if (volumeName3 is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName3);
            }
            if (volumeName4 is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName4);
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task MongoDBReplicaSetWithNoMembersAssigned()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rs = builder.AddMongoDBReplicaSet("rs0");

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(rs.Resource.Name, KnownResourceStates.FailedToStart, cts.Token);
    }

    private static async Task CreateTestDataWithReplicaSetFeaturesAsync(IMongoDatabase mongoDatabase, CancellationToken ct)
    {
        await mongoDatabase.CreateCollectionAsync(CollectionNameA, cancellationToken: ct);
        await mongoDatabase.CreateCollectionAsync(CollectionNameB, cancellationToken: ct);

        var moviesCollection = mongoDatabase.GetCollection<Movie>(CollectionNameA);
        var directorsCollection = mongoDatabase.GetCollection<Director>(CollectionNameB);

        // NOTE: Watch streams and transactions in MongoDB only work within replica sets; so if we successfully use both the aforementioned features, it is effectively verified that the replica set is functional.
        var directorsWatchCursor = await directorsCollection.WatchAsync(cancellationToken: ct);
        using var session = await mongoDatabase.Client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();

        await moviesCollection.InsertManyAsync(session, s_movies, cancellationToken: ct);
        await directorsCollection.InsertManyAsync(session, s_directors, cancellationToken: ct);

        await session.CommitTransactionAsync(ct);

        var results = await moviesCollection.Find(new BsonDocument()).ToListAsync(ct);

        Assert.Collection(results,
            item => Assert.Contains("The Shawshank Redemption", item.Name),
            item => Assert.Contains("The Godfather", item.Name),
            item => Assert.Contains("The Dark Knight", item.Name),
            item => Assert.Contains("Schindler's List", item.Name));

        await foreach (var item in directorsWatchCursor.ToAsyncEnumerable())
        {
            // NOTE: We only assert the first item
            Assert.Contains("Quentin Tarantino", item.FullDocument.Name);
            break;
        }
    }
}
