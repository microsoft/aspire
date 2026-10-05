// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using Polly;

#pragma warning disable ASPIREMONGODB001

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
    [RequiresFeature(TestFeature.DevCert)]
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

        // NOTE: The member has to reach healthy on its own, before anything is asked of the replica set. Its health check
        // and the initialization of the replica set must not depend on each other, or a fresh set can never come up.
        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo.Resource.Name, cts.Token);
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
    [RequiresFeature(TestFeature.DevCert)]
    public async Task VerifyReplicaSetInitializesWhenAMemberNeverBecomesHealthy()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // NOTE: This pins down the ordering that a replica set depends on. A member that carries `--replSet` has no primary
        // until this resource initiates the set against it, so anything the replica set waits for must be reachable without
        // the member being healthy first. Holding the member's health open forever is a deterministic stand-in for that,
        // and initialization has to complete regardless.
        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("held_open", () => healthCheckTcs.Task);

        var mongo = builder.AddMongoDB("mongo1").WithHealthCheck("held_open");
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

        var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);
        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(DbName);
        await CreateTestDataWithReplicaSetFeaturesAsync(db, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresFeature(TestFeature.DevCert)]
    public async Task VerifyMongoExpressConnectsToAReplicaSetMember()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 30, Delay = TimeSpan.FromSeconds(3) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // NOTE: Members of a replica set serve TLS and have no primary until the set has been initiated, so this covers the
        // companion admin UI against the hardest shape of MongoDB server this integration can produce.
        var mongoExpress = null as IResourceBuilder<MongoExpressContainerResource>;
        var mongo = builder.AddMongoDB("mongo1").WithMongoExpress(configureContainer: c => mongoExpress = c);
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo);

        Assert.NotNull(mongoExpress);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

        var endpoint = mongoExpress.Resource.GetEndpoint("http");
        using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

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
    [RequiresFeature(TestFeature.DevCert)]
    public async Task VerifyMongoDBMultiNodeReplicaSetAllNodesEndUpHealthy()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 30, Delay = TimeSpan.FromSeconds(3) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // NOTE: Mongo Express is part of this on purpose. A replica set member is the hardest server shape this integration
        // produces for it — TLS on, and no primary at all until the set has been initiated.
        var mongoExpress = null as IResourceBuilder<MongoExpressContainerResource>;
        var mongo1 = builder.AddMongoDB("mongo1").WithMongoExpress(configureContainer: c => mongoExpress = c);
        var mongo2 = builder.AddMongoDB("mongo2");
        var mongo3 = builder.AddMongoDB("mongo3");
        var rs = builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1)
            .WithMember(mongo2)
            .WithMember(mongo3);

        Assert.NotNull(mongoExpress);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo1.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo2.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo3.Resource.Name, cts.Token);

        Assert.True(mongo1.Resource.TlsEnabled);
        Assert.True(mongo2.Resource.TlsEnabled);
        Assert.True(mongo3.Resource.TlsEnabled);

        var mongoExpressEndpoint = mongoExpress.Resource.GetEndpoint("http");
        using var httpClient = new HttpClient { BaseAddress = new Uri(mongoExpressEndpoint.Url) };
        await pipeline.ExecuteAsync(async token =>
        {
            using var response = await httpClient.GetAsync("/", token);
            response.EnsureSuccessStatusCode();
        }, cts.Token);

        await app.StopAsync();
    }

    /// <summary>
    /// The ways in which the set of members of a replica set can change between two runs of the app host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MongoDB rejects a reconfiguration that gives an already-configured host a different <c>_id</c>, so every one of
    /// these has to leave the ids of the members that stayed untouched.
    /// </para>
    /// <para>
    /// Removing a member is deliberately not covered here: the id mapping itself handles it (see
    /// <c>BuildMembersConfigurationPreservesIdsOfExistingMembersWhenAMemberIsRemoved</c>), but a forced reconfiguration
    /// that both drops a member and moves the remaining members' split horizons leaves the surviving members unable to
    /// pick up the new configuration from each other, so the set would never elect a primary again. Until that is
    /// supported, removals against an initialized replica set are refused outright with an explanatory error.
    /// </para>
    /// </remarks>
    public enum TopologyChange
    {
        None,
        MemberAdded,
        MemberPrepended,
        MembersReordered,
    }

    [Theory]
    [InlineData(TopologyChange.None)]
    [InlineData(TopologyChange.MemberAdded)]
    [InlineData(TopologyChange.MemberPrepended)]
    [InlineData(TopologyChange.MembersReordered)]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresFeature(TestFeature.DevCert)]
    public async Task VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages(TopologyChange topologyChange)
    {
        // NOTE: This runs two complete app hosts in sequence, so each phase gets a budget of its own. Sharing one would let
        // a slow first phase eat into the second and fail it for no reason of its own.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var volumeName1 = null as string;
        var volumeName2 = null as string;
        var volumeName3 = null as string;
        var volumeName4 = null as string;
        var memberIdsByHost = null as Dictionary<string, int>;
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

                memberIdsByHost = await GetMemberIdsByHostAsync(client, cts.Token);
                Assert.Equal(3, memberIdsByHost.Count);

                await app.StopAsync();
            }

            using var secondPhaseCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using (var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper))
            {
                var passwordParameter = builder.AddParameter("mongoPassword", value: password!);

                // NOTE: The members are added to the application model lazily, so that a member that is dropped from the
                // replica set is not left behind as a standalone MongoDB server sitting on the data volume of a replica.
                var volumeNamesByMember = new Dictionary<string, string>
                {
                    ["mongo1"] = volumeName1,
                    ["mongo2"] = volumeName2,
                    ["mongo3"] = volumeName3,
                };
                IResourceBuilder<MongoDBServerResource> AddMember(string name) =>
                    builder.AddMongoDB(name).WithDataVolume(volumeNamesByMember[name]);

                var rs = builder.AddMongoDBReplicaSet("rs0", password: passwordParameter);

                switch (topologyChange)
                {
                    case TopologyChange.None:
                        rs = rs.WithMember(AddMember("mongo1")).WithMember(AddMember("mongo2")).WithMember(AddMember("mongo3"));
                        break;

                    case TopologyChange.MemberAdded:
                    case TopologyChange.MemberPrepended:
                        {
                            var mongo4 = builder.AddMongoDB("mongo4");
                            volumeName4 = VolumeNameGenerator.Generate(mongo4, nameof(VerifyMongoDBMultiNodeReplicaWithDataShouldWorkAcrossUsages));
                            // NOTE: If the volume already exists (because of a crashing previous run), delete it.
                            DockerUtils.AttemptDeleteDockerVolume(volumeName4);
                            mongo4 = mongo4.WithDataVolume(volumeName4);

                            if (topologyChange is TopologyChange.MemberPrepended)
                            {
                                rs = rs.WithMember(mongo4);
                            }

                            rs = rs.WithMember(AddMember("mongo1")).WithMember(AddMember("mongo2")).WithMember(AddMember("mongo3"));

                            if (topologyChange is TopologyChange.MemberAdded)
                            {
                                rs = rs.WithMember(mongo4);
                            }
                            break;
                        }

                    case TopologyChange.MembersReordered:
                        rs = rs.WithMember(AddMember("mongo3")).WithMember(AddMember("mongo1")).WithMember(AddMember("mongo2"));
                        break;
                }

                using var app = builder.Build();
                await app.StartAsync(secondPhaseCts.Token);

                await app.ResourceNotifications.WaitForResourceHealthyAsync(rs.Resource.Name, secondPhaseCts.Token);

                var connectionString = await rs.Resource.ConnectionStringExpression.GetValueAsync(secondPhaseCts.Token);
                var client = new MongoClient(connectionString);
                var db = client.GetDatabase(DbName);
                var moviesCollection = db.GetCollection<Movie>(CollectionNameA);
                var data = await moviesCollection.Find(_ => true).SortBy(e => e.Name).ToListAsync(secondPhaseCts.Token);
                Assert.Collection(data,
                    item => Assert.Equal("Schindler's List", item.Name),
                    item => Assert.Equal("The Dark Knight", item.Name),
                    item => Assert.Equal("The Godfather", item.Name),
                    item => Assert.Equal("The Shawshank Redemption", item.Name)
                );

                // NOTE: MongoDB rejects a reconfiguration that gives an already-configured host a different `_id`, so the
                // ids of the members that were carried over have to be exactly the ones they had in the previous run. This
                // asserts it against the configuration the server actually ended up with, not just the one we sent.
                var currentMemberIdsByHost = await GetMemberIdsByHostAsync(client, secondPhaseCts.Token);
                foreach (var (host, id) in memberIdsByHost!)
                {
                    Assert.Equal(id, currentMemberIdsByHost[host]);
                }

                if (topologyChange is TopologyChange.MemberAdded or TopologyChange.MemberPrepended)
                {
                    // NOTE: The new member must not have reused an id that was already taken.
                    Assert.Equal(4, currentMemberIdsByHost.Count);
                    Assert.Equal(currentMemberIdsByHost.Values.Distinct().Count(), currentMemberIdsByHost.Count);
                }
                else
                {
                    Assert.Equal(memberIdsByHost.Count, currentMemberIdsByHost.Count);
                }

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

    /// <summary>
    /// Reads the replica set configuration the server is actually running with, as a map of member host to member id.
    /// </summary>
    private static async Task<Dictionary<string, int>> GetMemberIdsByHostAsync(IMongoClient client, CancellationToken ct)
    {
        var config = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument { ["replSetGetConfig"] = 1 },
            cancellationToken: ct);

        return config["config"]["members"].AsBsonArray
            .OfType<BsonDocument>()
            .ToDictionary(m => m["host"].AsString, m => m["_id"].AsInt32, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task CreateTestDataWithReplicaSetFeaturesAsync(IMongoDatabase mongoDatabase, CancellationToken ct)
    {
        // NOTE: This runs inside a resilience pipeline, so it has to be able to start over. Dropping the collections first
        // makes the whole helper idempotent; otherwise a transient failure part-way through would turn every subsequent
        // attempt into a `NamespaceExists` failure on collection creation, or duplicate the inserted documents.
        await mongoDatabase.DropCollectionAsync(CollectionNameA, cancellationToken: ct);
        await mongoDatabase.DropCollectionAsync(CollectionNameB, cancellationToken: ct);

        await mongoDatabase.CreateCollectionAsync(CollectionNameA, cancellationToken: ct);
        await mongoDatabase.CreateCollectionAsync(CollectionNameB, cancellationToken: ct);

        var moviesCollection = mongoDatabase.GetCollection<Movie>(CollectionNameA);
        var directorsCollection = mongoDatabase.GetCollection<Director>(CollectionNameB);

        // NOTE: Watch streams and transactions in MongoDB only work within replica sets; so if we successfully use both the aforementioned features, it is effectively verified that the replica set is functional.
        using var directorsWatchCursor = await directorsCollection.WatchAsync(cancellationToken: ct);
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

        // NOTE: The cursor is advanced directly rather than through `ToAsyncEnumerable()`, whose adapter does not carry a
        // cancellation token, so that missing the change event fails the test on its own timeout instead of hanging.
        // NOTE: A change stream cursor yields empty batches while it waits, so an empty `Current` is not the end of it.
        var observedChange = null as ChangeStreamDocument<Director>;
        while (observedChange is null && await directorsWatchCursor.MoveNextAsync(ct))
        {
            observedChange = directorsWatchCursor.Current.FirstOrDefault();
        }

        // NOTE: Asserted after the loop rather than inside it. A cursor that closed without ever yielding a change would
        // otherwise fall straight out of the loop and leave the watch stream unverified while the test still passed.
        Assert.NotNull(observedChange);
        // NOTE: We only assert the first item
        Assert.Contains("Quentin Tarantino", observedChange.FullDocument.Name);
    }
}
