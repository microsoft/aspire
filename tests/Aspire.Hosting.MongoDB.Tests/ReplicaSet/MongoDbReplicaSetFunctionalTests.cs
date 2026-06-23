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
