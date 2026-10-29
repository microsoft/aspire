// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting.MongoDB.Tests;

public class SingleMemberReplicaSetTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingKeyFileParameterBlocksOnlyTheServer(bool compositeValue)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestDcpExecutor(options => options.DisableDashboard = false, testOutputHelper);
        var key = builder.AddParameter("key", secret: true);
        IExpressionValue value = compositeValue ? ReferenceExpression.Create($"prefix-{key}") : key.Resource;
        var mongo = builder.AddMongoDB("mongo").WithKeyFile(value);
        var consumer = builder.AddContainer("consumer", "test-image").WithReference(mongo);
        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestratorProxy>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        Assert.True(app.Services.GetRequiredService<IInteractionService>().IsAvailable);
        await orchestrator.PublishResourcesPreparedAsync(cts.Token);
        await orchestrator.PublishResourceStartingAsync(mongo.Resource, cts.Token);
        await orchestrator.PublishResourceStartingAsync(consumer.Resource, cts.Token);

        Assert.True(notifications.TryGetCurrentState(mongo.Resource.Name, out var unresolved));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolved.Snapshot.State?.Text);
        Assert.Equal(
            ["key"],
            Assert.IsType<string[]>(Assert.Single(unresolved.Snapshot.Properties, p => p.Name == "resource.unresolvedParameters").Value));
        Assert.True(notifications.TryGetCurrentState(consumer.Resource.Name, out var consumerState));
        Assert.Equal(KnownResourceStates.Starting, consumerState.Snapshot.State?.Text);
        Assert.Empty(consumerState.Snapshot.Properties.Where(p => p.Name == "resource.unresolvedParameters"));

        var files = Assert.Single(mongo.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        var filesTask = files.Callback(new ContainerFileSystemCallbackContext
        {
            Model = mongo.Resource,
            Services = app.Services
        }, cts.Token);
        Assert.False(filesTask.IsCompleted);

        await key.Resource.SetValueAsync("shared-key", cts.Token);
        var file = Assert.IsType<ContainerFile>(Assert.Single(await filesTask));
        Assert.Equal("rs.key", file.Name);
        Assert.Equal(compositeValue ? "prefix-shared-key" : "shared-key", file.Contents);
        var starting = await notifications.WaitForResourceAsync(mongo.Resource.Name, e => e.Snapshot.State?.Text == KnownResourceStates.Starting, cts.Token);
        Assert.Empty(starting.Snapshot.Properties.Where(p => p.Name == "resource.unresolvedParameters"));
    }

    [Fact]
    public async Task WithReplicaSetKeepsTheServerAndDatabaseModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo = builder.AddMongoDB("mongo");
        var password = mongo.Resource.PasswordParameter;
        var database = mongo.AddDatabase("orders");

        Assert.Same(mongo, mongo.WithReplicaSet().WithReplicaSet("mongo").WithReplicaSet());
        Assert.Equal("mongo", mongo.Resource.ReplicaSetName);
        Assert.Same(password, mongo.Resource.PasswordParameter);
        Assert.Same(mongo.Resource, database.Resource.Parent);
        Assert.Equal(["mongo", "orders"], builder.Resources.Where(r => r is not ParameterResource).Select(r => r.Name));
        Assert.Equal(["--keyFile", "/etc/rs.key", "--bind_ip_all", "--replSet", "mongo"],
            await ArgumentEvaluator.GetArgumentListAsync(mongo.Resource));
        var endpoint = Assert.Single(mongo.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Null(endpoint.Port);
        Assert.Equal(27017, endpoint.TargetPort);
        Assert.True(endpoint.IsProxied);
        Assert.Single(mongo.Resource.Annotations.OfType<MongoDBSingleMemberReplicaSetAnnotation>());
        Assert.True(Assert.IsType<ParameterResource>(
            Assert.Single(mongo.Resource.Annotations.OfType<MongoDBServerKeyFileAnnotation>()).Value).Secret);
    }

    [Fact]
    public async Task WithReplicaSetHonorsAnExplicitKeyFileAndSetName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var key = builder.AddParameter("key", "sharedkey", secret: true);
        var mongo = builder.AddMongoDB("mongo").WithKeyFile(key.Resource, "/etc/custom.key").WithReplicaSet("rs0").WithReplicaSet();

        Assert.Equal("rs0", mongo.Resource.ReplicaSetName);
        Assert.Same(key.Resource, Assert.Single(mongo.Resource.Annotations.OfType<MongoDBServerKeyFileAnnotation>()).Value);
        Assert.Equal(["--keyFile", "/etc/custom.key", "--bind_ip_all", "--replSet", "rs0"],
            await ArgumentEvaluator.GetArgumentListAsync(mongo.Resource));
        Assert.Equal(["key"], builder.Resources.OfType<ParameterResource>().Select(r => r.Name));
    }

    [Fact]
    public void ReplicaSetNamesAreCaseSensitive()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo = builder.AddMongoDB("mongo").WithReplicaSet("rs0");
        var annotations = mongo.Resource.Annotations.ToArray();

        Assert.Throws<InvalidOperationException>(() => mongo.WithReplicaSet("RS0"));
        Assert.Equal(annotations, mongo.Resource.Annotations);
        Assert.Equal("rs0", mongo.Resource.ReplicaSetName);
    }

    [Theory]
    [InlineData("rs0")]
    [InlineData("advanced")]
    public void AdvancedSetCannotAdoptASingleMember(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo = builder.AddMongoDB("mongo").WithReplicaSet("rs0");
        var advanced = builder.AddMongoDBReplicaSet(name);
        var annotations = mongo.Resource.Annotations.ToArray();
        var password = mongo.Resource.PasswordParameter;

        var exception = Assert.Throws<InvalidOperationException>(() => advanced.WithMember(mongo));
        Assert.Contains("Remove WithReplicaSet", exception.Message);
        Assert.Equal(annotations, mongo.Resource.Annotations);
        Assert.Same(password, mongo.Resource.PasswordParameter);
        Assert.Empty(advanced.Resource.Members);
    }

    [Fact]
    public void SingleMemberCannotInitializeAnAdvancedMember()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo = builder.AddMongoDB("mongo");
        var advanced = builder.AddMongoDBReplicaSet("advanced").WithMember(mongo);
        var annotations = mongo.Resource.Annotations.ToArray();

        Assert.Throws<InvalidOperationException>(() => mongo.WithReplicaSet());
        Assert.Equal(annotations, mongo.Resource.Annotations);
        Assert.Same(mongo.Resource, Assert.Single(advanced.Resource.Members));
    }

    [Fact]
    public void DefaultReplicaSetThrowsBeforeMutatingInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var mongo = builder.AddMongoDB("mongo");
        var annotations = mongo.Resource.Annotations.ToArray();
        var resources = builder.Resources.ToArray();

        Assert.Throws<NotSupportedException>(() => mongo.WithReplicaSet());
        Assert.Equal(annotations, mongo.Resource.Annotations);
        Assert.Equal(resources, builder.Resources);
    }

    [Fact]
    public async Task SingleMemberWithoutDeveloperCertificateKeepsSecureAuthenticationAndDirectPrimaryConnections()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            [], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: false));
        var password = builder.AddParameter("password", "p@ssword", secret: true);
        var mongo = builder.AddMongoDB("mongo", password: password).WithReplicaSet()
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 43210));
        var database = mongo.AddDatabase("orders");

        using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.False(mongo.Resource.TlsEnabled);
        Assert.False(mongo.Resource.TlsAllowInvalidCertificates);
        Assert.Equal(["--keyFile", "/etc/rs.key", "--bind_ip_all", "--replSet", "mongo"],
            await ArgumentEvaluator.GetArgumentListAsync(mongo.Resource));

        foreach (IResourceWithConnectionString resource in new IResourceWithConnectionString[] { mongo.Resource, database.Resource })
        {
            var connectionString = await resource.GetConnectionStringAsync();
            var url = new MongoUrl(connectionString);
            Assert.True(url.DirectConnection);
            Assert.Equal(ReadPreference.Primary, MongoClientSettings.FromUrl(url).ReadPreference);
            Assert.Equal("admin", url.Username);
            Assert.Equal("p@ssword", url.Password);
            Assert.Equal("localhost:43210", url.Server.ToString());
            Assert.Equal(resource == database.Resource ? "orders" : null, url.DatabaseName);
            Assert.Equal(connectionString, await resource.GetConnectionProperties().Single(p => p.Key == "Uri").Value.GetValueAsync(default));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A resource-specific initialization failure.")]
    public async Task HealthIsUnhealthyBeforeInitializationOrAfterFailure(string? error)
    {
        using var client = new MongoClient();
        var annotation = new MongoDBSingleMemberReplicaSetAnnotation("mongo");
        if (error is not null)
        {
            annotation.InitializationError = error;
        }

        var check = new MyMongoDbHealthCheck(client, "admin", annotation);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(error ?? "The single-member replica set has not been initialized.", result.Description);
    }

    [Fact]
    public void ExistingSingleMemberConfigurationIsNotMutated()
    {
        var config = BsonDocument.Parse("""{ "_id": "mongo", "version": 7, "members": [{ "_id": 0, "host": "localhost:27017", "priority": 1 }], "settings": { "heartbeatIntervalMillis": 2000 } }""");
        var original = config.DeepClone();

        MongoDBSingleMemberReplicaSet.ValidateConfiguration(config, "mongo", "localhost:27017");

        Assert.Equal(original, config);
    }

    [Theory]
    [InlineData("""{ "_id": "other", "members": [{ "host": "localhost:27017" }] }""")]
    [InlineData("""{ "_id": "mongo", "members": [{ "host": "old-host:27017" }] }""")]
    [InlineData("""{ "_id": "mongo", "members": [{ "host": "localhost:27017" }, { "host": "other:27017" }] }""")]
    public void IncompatibleDataIsRejectedWithoutReconfiguration(string json)
    {
        var config = BsonDocument.Parse(json);
        var original = config.DeepClone();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            MongoDBSingleMemberReplicaSet.ValidateConfiguration(config, "mongo", "localhost:27017"));

        Assert.Contains("automatic migration or reconfiguration is not supported", exception.Message);
        Assert.Equal(original, config);
    }

    [Theory]
    [InlineData("mongo", 1, 1, true)]
    [InlineData("mongo", 2, 1, false)]
    [InlineData("other", 1, 1, false)]
    [InlineData("mongo", 1, 2, false)]
    public void ReadinessRequiresTheSingleMemberPrimary(string name, int state, int memberCount, bool expected)
    {
        var status = new BsonDocument
        {
            ["set"] = name,
            ["myState"] = state,
            ["members"] = new BsonArray(Enumerable.Range(0, memberCount).Select(id => new BsonDocument("_id", id))),
        };

        Assert.Equal(expected, MongoDBSingleMemberReplicaSet.IsPrimary(status, "mongo"));
    }

    [Fact]
    public async Task InitializationHonorsCancellationBeforeSendingCommands()
    {
        using var client = new MongoClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MongoDBSingleMemberReplicaSet.InitializeAndWaitForPrimaryAsync(client.GetDatabase("admin"), "mongo", "localhost:27017", cancellation.Token));
    }
}
