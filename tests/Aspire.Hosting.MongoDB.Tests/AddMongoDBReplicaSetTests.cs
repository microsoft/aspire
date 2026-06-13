// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.MongoDB.Tests;

public class AddMongoDBReplicaSetTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddMongoDBReplicaSetAddsHealthCheckAnnotationToResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var rs = builder.AddMongoDBReplicaSet("rs0");
        Assert.Single(rs.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "rs0_check");
    }

    [Fact]
    public void AddMongoDBReplicaSetAddsResourceToModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddMongoDBReplicaSet("rs0");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<MongoDBReplicaSetResource>());
        Assert.Equal("rs0", resource.Name);
    }

    [Fact]
    public void WithMemberAddsWaitAnnotationForMember()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        var rs = builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1);

        Assert.Single(rs.Resource.Annotations.OfType<WaitAnnotation>(),
            a => a.Resource == mongo1.Resource);
    }

    [Fact]
    public void WithMemberConfiguresServerWithReplSetArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1);

        var argsAnnotation = Assert.Single(mongo1.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>());
        var args = new List<object>();
        argsAnnotation.Callback(new CommandLineArgsCallbackContext(args));
        Assert.Contains("--replSet", args);
        Assert.Contains("rs0", args);
    }

    [Fact]
    public async Task ReplicaSetConnectionStringHasCorrectFormat()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));
        var mongo2 = builder.AddMongoDB("mongo2")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27018));

        var rs = builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1)
            .WithMember(mongo2);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var replicaSetResource = Assert.Single(appModel.Resources.OfType<MongoDBReplicaSetResource>());

        var connectionString = await replicaSetResource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);

        // Must have no trailing comma before the query string delimiter, correct replicaSet name (not member count)
        Assert.Equal("mongodb://localhost:27017,localhost:27018/?replicaSet=rs0", connectionString);
    }

    [Fact]
    public async Task ReplicaSetConnectionStringWithSingleMemberHasNoTrailingComma()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));

        var rs = builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var replicaSetResource = Assert.Single(appModel.Resources.OfType<MongoDBReplicaSetResource>());

        var connectionString = await replicaSetResource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);

        Assert.DoesNotContain(",?", connectionString);
        Assert.Equal("mongodb://localhost:27017/?replicaSet=rs0", connectionString);
    }

    [Fact]
    public void ConnectionStringExpressionThrowsWhenNoMembersAdded()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var rs = builder.AddMongoDBReplicaSet("rs0");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var replicaSetResource = Assert.Single(appModel.Resources.OfType<MongoDBReplicaSetResource>());

        Assert.Throws<InvalidOperationException>(() => replicaSetResource.ConnectionStringExpression);
    }

    [Fact]
    public void AddMongoDBReplicaSetThrowsWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "rs0";

        var action = () => builder.AddMongoDBReplicaSet(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddMongoDBReplicaSetThrowsWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddMongoDBReplicaSet(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithMemberThrowsWhenBuilderIsNull()
    {
        IResourceBuilder<MongoDBReplicaSetResource> builder = null!;
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var member = appBuilder.AddMongoDB("mongo1");

        var action = () => builder.WithMember(member);

        Assert.Throws<NullReferenceException>(action);
    }
}
