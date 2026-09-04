// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting.MongoDB.Tests;

public class SingleMemberReplicaSetFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public Task TransactionsAndChangeStreamsWorkWithoutADeveloperCertificate() => VerifyFeaturesAsync(useTls: false);

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresFeature(TestFeature.DevCert)]
    public Task TransactionsAndChangeStreamsWorkWithTls() => VerifyFeaturesAsync(useTls: true);

    private async Task VerifyFeaturesAsync(bool useTls)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        if (!useTls)
        {
            builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
                [], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: false));
        }

        var mongo = builder.AddMongoDB("mongo").WithReplicaSet();
        if (useTls)
        {
            mongo.WithHttpsDeveloperCertificate();
        }

        var database = mongo.AddDatabase("orders");
        using var app = builder.Build();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.StartAsync(startup.Token);
        using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.ResourceNotifications.WaitForResourceHealthyAsync(database.Resource.Name, ready.Token);
        Assert.Equal(useTls, mongo.Resource.TlsEnabled);

        using var operations = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var serverClient = new MongoClient(await mongo.Resource.ConnectionStringExpression.GetValueAsync(operations.Token));
        using var databaseClient = new MongoClient(await database.Resource.ConnectionStringExpression.GetValueAsync(operations.Token));
        await VerifyTransactionsAndChangeStreamsAsync(serverClient.GetDatabase("serverdb"), operations.Token);
        await VerifyTransactionsAndChangeStreamsAsync(databaseClient.GetDatabase("orders"), operations.Token);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task InitializationDoesNotWaitForHealthAndWaitForGatesContainerClients(bool waitForDatabase)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var gate = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddHealthChecks().AddCheck("held", () =>
            gate.Task.IsCompletedSuccessfully ? gate.Task.Result : HealthCheckResult.Unhealthy("Held until the test releases readiness."));
        var mongo = builder.AddMongoDB("mongo").WithoutHttpsCertificate().WithReplicaSet().WithHealthCheck("held");
        var database = mongo.AddDatabase("orders");

        // This is a real container consumer of the normal database reference. Its address is translated to the
        // container network, not the host's random proxy port, and it must be able to commit immediately after WaitFor.
        var consumer = builder.AddContainer("consumer", MongoDBContainerImageTags.Image, MongoDBContainerImageTags.Tag)
            .WithImageRegistry(MongoDBContainerImageTags.Registry)
            .WithEntrypoint("mongosh")
            .WithReference(database)
            .WithArgs("--quiet", "--nodb", "--eval", """
                const connection = new Mongo(process.env.ConnectionStrings__orders);
                const session = connection.startSession();
                session.startTransaction();
                session.getDatabase("orders").items.insertOne({ _id: 1, name: "container" });
                session.commitTransaction();
                session.endSession();
                """);
        if (waitForDatabase)
        {
            consumer.WaitFor(database);
        }
        else
        {
            consumer.WaitFor(mongo);
        }

        using var app = builder.Build();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pendingStart = app.StartAsync(startup.Token);
        using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.ResourceNotifications.WaitForResourceAsync(mongo.Resource.Name,
            update => update.Snapshot.HealthReports.Any(r => r.Name == "mongo_check" && r.Status == HealthStatus.Healthy), ready.Token);
        await app.ResourceNotifications.WaitForResourceAsync(consumer.Resource.Name, KnownResourceStates.Waiting, ready.Token);

        gate.SetResult(HealthCheckResult.Healthy());
        await pendingStart;
        var completed = await app.ResourceNotifications.WaitForResourceAsync(consumer.Resource.Name,
            update => update.Snapshot.State?.Text == KnownResourceStates.Exited, ready.Token);
        Assert.Equal(0, completed.Snapshot.ExitCode);

        using var client = new MongoClient(await database.Resource.ConnectionStringExpression.GetValueAsync(ready.Token));
        var item = await client.GetDatabase("orders").GetCollection<BsonDocument>("items").Find(new BsonDocument("_id", 1)).SingleAsync(ready.Token);
        Assert.Equal("container", item["name"].AsString);
        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task DataAndReplicaSetIdentitySurviveNewAppHostsAndPorts()
    {
        using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var mongo1 = builder1.AddMongoDB("mongo").WithoutHttpsCertificate().WithReplicaSet("orders-rs");
        var volumeName = VolumeNameGenerator.Generate(mongo1, nameof(DataAndReplicaSetIdentitySurviveNewAppHostsAndPorts));
        DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
        mongo1.WithDataVolume(volumeName);

        string password;
        BsonDocument originalConfig;
        int originalPort;
        try
        {
            using (var app = builder1.Build())
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await app.StartAsync(startup.Token);
                using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var running = await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo1.Resource.Name, ready.Token);
                password = (await mongo1.Resource.PasswordParameter!.GetValueAsync(ready.Token))!;
                originalPort = mongo1.Resource.PrimaryEndpoint.Port;
                using var client = new MongoClient(await mongo1.Resource.ConnectionStringExpression.GetValueAsync(ready.Token));
                await VerifyTransactionsAndChangeStreamsAsync(client.GetDatabase("orders"), ready.Token);
                originalConfig = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetConfig", 1), cancellationToken: ready.Token);

                using var restart = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var orchestrator = app.Services.GetRequiredService<ApplicationOrchestratorProxy>();
                await orchestrator.StopResourceAsync(running.ResourceId, restart.Token);
                await app.ResourceNotifications.WaitForResourceAsync(mongo1.Resource.Name, KnownResourceStates.Exited, restart.Token);
                await orchestrator.StartResourceAsync(running.ResourceId, restart.Token);
                await app.ResourceNotifications.WaitForResourceAsync(mongo1.Resource.Name, KnownResourceStates.Running, restart.Token);
                await app.ResourceNotifications.WaitForResourceHealthyAsync(mongo1.Resource.Name, restart.Token);
                await VerifyTransactionsAndChangeStreamsAsync(client.GetDatabase("aftercontainerrestart"), restart.Token);
                await app.StopAsync();
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var mongo2 = builder2.AddMongoDB("mongo", password: builder2.AddParameter("password", password, secret: true))
                .WithoutHttpsCertificate().WithReplicaSet("orders-rs").WithDataVolume(volumeName);
            using var app2 = builder2.Build();
            using var secondStartup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await app2.StartAsync(secondStartup.Token);
            using var secondReady = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await app2.ResourceNotifications.WaitForResourceHealthyAsync(mongo2.Resource.Name, secondReady.Token);
            testOutputHelper.WriteLine($"Host ports across AppHost runs: {originalPort}, {mongo2.Resource.PrimaryEndpoint.Port}");

            using var secondClient = new MongoClient(await mongo2.Resource.ConnectionStringExpression.GetValueAsync(secondReady.Token));
            var currentConfig = await secondClient.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetConfig", 1), cancellationToken: secondReady.Token);
            // Elections advance the term without changing the configuration version or replicaSetId.
            Assert.True(currentConfig["config"]["term"].ToInt64() >= originalConfig["config"]["term"].ToInt64());
            originalConfig["config"].AsBsonDocument.Remove("term");
            currentConfig["config"].AsBsonDocument.Remove("term");
            Assert.Equal(originalConfig["config"], currentConfig["config"]);
            var item = await secondClient.GetDatabase("orders").GetCollection<BsonDocument>("items").Find(new BsonDocument("_id", 1)).SingleAsync(secondReady.Token);
            Assert.Equal("committed", item["name"].AsString);
            await VerifyTransactionsAndChangeStreamsAsync(secondClient.GetDatabase("afterrestart"), secondReady.Token);
            await app2.StopAsync();
        }
        finally
        {
            DockerUtils.AttemptDeleteDockerVolume(volumeName);
        }
    }

    private static async Task VerifyTransactionsAndChangeStreamsAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        await database.CreateCollectionAsync("items", cancellationToken: cancellationToken);
        var collection = database.GetCollection<BsonDocument>("items");
        using var changes = await collection.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        session.StartTransaction();
        await collection.InsertOneAsync(session, new BsonDocument { ["_id"] = 1, ["name"] = "committed" }, cancellationToken: cancellationToken);
        await session.CommitTransactionAsync(cancellationToken);

        var item = await collection.Find(new BsonDocument("_id", 1)).SingleAsync(cancellationToken);
        Assert.Equal("committed", item["name"].AsString);
        while (await changes.MoveNextAsync(cancellationToken))
        {
            if (changes.Current.FirstOrDefault() is { } change)
            {
                Assert.Equal(ChangeStreamOperationType.Insert, change.OperationType);
                Assert.Equal(item, change.FullDocument);
                return;
            }
        }

        Assert.Fail("The committed transaction did not produce a change stream event.");
    }
}
