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
    private const string CollectionName = "movie_collection";

    private static readonly Movie[] s_movies =
    [
        new() { Name = "The Shawshank Redemption"},
        new() { Name = "The Godfather"},
        new() { Name = "The Dark Knight"},
        new() { Name = "Schindler's List"},
    ];

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMongoDBReplicaSetResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 20, Delay = TimeSpan.FromSeconds(3) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var mongo = builder.AddMongoDB("mongo1");
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(rs.Resource.Name, KnownResourceStates.Running, cts.Token);

        var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

        await pipeline.ExecuteAsync(async token =>
        {
            var client = new MongoClient(connectionString);
            var db = client.GetDatabase(DbName);
            await CreateTestDataAsync(db, token);
        }, cts.Token);

        await app.StopAsync();
    }

    // [Fact]
    // [RequiresFeature(TestFeature.Docker)]
    // public async Task VerifyReplSetGetConfigReturnsValidConfiguration()
    // {
    //     // Test that verifies the replica set configuration is correctly initialized
    //     // by running the replSetGetConfig command and validating the response.
    //     var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    //     var pipeline = new ResiliencePipelineBuilder()
    //         .AddRetry(new() { MaxRetryAttempts = 20, Delay = TimeSpan.FromSeconds(3) })
    //         .Build();

    //     using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

    //     var mongo1 = builder.AddMongoDB("mongo1");

    //     var rs = builder.AddMongoDBReplicaSet("rs0")
    //         .WithMember(mongo1);

    //     using var app = builder.Build();
    //     await app.StartAsync(cts.Token);

    //     // Wait for the replica set health check to pass, meaning replSetInitiate has completed
    //     await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

    //     var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

    //     await pipeline.ExecuteAsync(async token =>
    //     {
    //         var client = new MongoClient(connectionString);
    //         var admin = client.GetDatabase("admin");

    //         // Run replSetGetConfig to retrieve the replica set configuration
    //         var config = await admin.RunCommandAsync<BsonDocument>(
    //             new BsonDocument { ["replSetGetConfig"] = 1 },
    //             cancellationToken: token
    //         );

    //         // Verify that the configuration exists and has the expected structure
    //         Assert.NotNull(config);
    //         Assert.True(config.Contains("config"), "Configuration should contain 'config' field");

    //         var configDoc = config["config"].AsBsonDocument;
    //         Assert.NotNull(configDoc);

    //         // Verify the replica set name matches what we configured
    //         Assert.Equal("rs0", configDoc["_id"].AsString);

    //         // Verify the members array exists and has the correct count (single member)
    //         Assert.True(configDoc.Contains("members"), "Configuration should contain 'members' field");
    //         var members = configDoc["members"].AsBsonArray;
    //         Assert.Single(members);

    //         // Verify the single member has the required fields
    //         var memberDoc = members[0].AsBsonDocument;
    //         Assert.True(memberDoc.Contains("_id"), "Member should have '_id' field");
    //         Assert.True(memberDoc.Contains("host"), "Member should have 'host' field");
    //         Assert.Equal(0, memberDoc["_id"].AsInt32);

    //         // Verify the version field exists
    //         Assert.True(configDoc.Contains("version"), "Configuration should contain 'version' field");
    //         var version = configDoc["version"].AsInt32;
    //         Assert.True(version >= 1, "Configuration version should be at least 1");
    //     }, cts.Token);

    //     await app.StopAsync();
    // }

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
            item => Assert.Contains("Schindler's List", item.Name));
    }
}
