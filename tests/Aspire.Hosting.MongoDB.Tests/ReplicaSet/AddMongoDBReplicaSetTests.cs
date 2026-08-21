// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIRECERTIFICATES001

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
    public async Task WithMemberConfiguresServerWithReplSetArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        builder.AddMongoDBReplicaSet("rs0")
            .WithMember(mongo1);

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Contains("--replSet", args);
        Assert.Contains("rs0", args);
    }

    [Fact]
    public async Task ReplicaSetConnectionStringHasCorrectContents()
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

        var connectionStringObj = new MongoUrl(connectionString);
        Assert.Equal("rs0", connectionStringObj.ReplicaSetName);
        Assert.Equal(["localhost:27017", "localhost:27018"], connectionStringObj.Servers.Select(s => s.ToString()));
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

        var connectionStringObj = new MongoUrl(connectionString);
        Assert.Equal("rs0", connectionStringObj.ReplicaSetName);
        Assert.Equal(["localhost:27017"], connectionStringObj.Servers.Select(s => s.ToString()));
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
    public void AddMongoDBReplicaSetThrowsInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        Assert.Throws<NotSupportedException>(() => builder.AddMongoDBReplicaSet("rs0"));
    }

    [Fact]
    public void WithMemberOptsTheMemberInToTheDeveloperCertificate()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);

        var annotation = Assert.Single(mongo1.Resource.Annotations.OfType<HttpsCertificateAnnotation>());
        Assert.True(annotation.UseDeveloperCertificate);
    }

    [Fact]
    public void WithMemberKeepsTheMembersOwnCertificateConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var certificatePassword = builder.AddParameter("cert-password", "test123");
        var mongo1 = builder.AddMongoDB("mongo1").WithHttpsDeveloperCertificate(certificatePassword);
        builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);

        var annotation = Assert.Single(mongo1.Resource.Annotations.OfType<HttpsCertificateAnnotation>());
        Assert.Equal(certificatePassword.Resource, annotation.Password);
    }

    [Fact]
    public async Task WithMemberAllowsInvalidCertificatesForIntraClusterConnections()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        using var certificate = CreateTestCertificate();
        var mongo1 = builder.AddMongoDB("mongo1").WithHttpsCertificate(certificate);
        builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Contains("--tlsAllowInvalidCertificates", args);
    }

    [Fact]
    public void WithMemberThrowsWhenTheSameMemberIsAddedTwice()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);

        var action = () => rs.WithMember(mongo1);

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("already been added as a member", exception.Message);
    }

    [Fact]
    public void WithMemberThrowsWhenTheMemberBelongsToAnotherReplicaSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);
        var otherReplicaSet = builder.AddMongoDBReplicaSet("rs1");

        var action = () => otherReplicaSet.WithMember(mongo1);

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("already a member of the replica set 'rs0'", exception.Message);
    }

    [Fact]
    public void WithMemberThrowsWhenTheMemberHasAConflictingPassword()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var memberPassword = builder.AddParameter("member-password", "p@ssw0rd", secret: true);
        var mongo1 = builder.AddMongoDB("mongo1", password: memberPassword);
        var rs = builder.AddMongoDBReplicaSet("rs0");

        var action = () => rs.WithMember(mongo1);

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("explicit password", exception.Message);
        Assert.Contains(nameof(MongoDBReplicaSetBuilderExtensions.AddMongoDBReplicaSet), exception.Message);
    }

    [Fact]
    public void WithMemberThrowsWhenTheMemberHasAConflictingUserName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var memberUserName = builder.AddParameter("member-username", "someone");
        var mongo1 = builder.AddMongoDB("mongo1", userName: memberUserName);
        var rs = builder.AddMongoDBReplicaSet("rs0");

        var action = () => rs.WithMember(mongo1);

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("explicit user name", exception.Message);
    }

    [Fact]
    public void WithMemberAcceptsAMemberThatSharesTheReplicaSetCredentials()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var userName = builder.AddParameter("shared-username", "someone");
        var password = builder.AddParameter("shared-password", "p@ssw0rd", secret: true);
        var mongo1 = builder.AddMongoDB("mongo1", userName: userName, password: password);

        var rs = builder.AddMongoDBReplicaSet("rs0", userName: userName, password: password).WithMember(mongo1);

        Assert.Same(userName.Resource, mongo1.Resource.UserNameParameter);
        Assert.Same(password.Resource, mongo1.Resource.PasswordParameter);
        Assert.Single(rs.Resource.Members);
    }

    [Fact]
    public void WithMemberAdoptsTheReplicaSetCredentialsWhenTheMemberHasGeneratedOnes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1");
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);

        Assert.Same(rs.Resource.SharedPasswordParameter, mongo1.Resource.PasswordParameter);
    }

    [Fact]
    public async Task ReplicaSetExposesConnectionProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));
        var rs = builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1);

        var properties = ((IResourceWithConnectionString)rs.Resource).GetConnectionProperties().ToArray();

        Assert.Equal(
            ["Username", "Password", "AuthenticationDatabase", "AuthenticationMechanism", "ReplicaSetName", "Uri"],
            properties.Select(p => p.Key));
        Assert.Equal("admin", await properties.Single(p => p.Key == "Username").Value.GetValueAsync(CancellationToken.None));
        Assert.Equal("admin", await properties.Single(p => p.Key == "AuthenticationDatabase").Value.GetValueAsync(CancellationToken.None));
        Assert.Equal("SCRAM-SHA-256", await properties.Single(p => p.Key == "AuthenticationMechanism").Value.GetValueAsync(CancellationToken.None));
        Assert.Equal("rs0", await properties.Single(p => p.Key == "ReplicaSetName").Value.GetValueAsync(CancellationToken.None));

        var uri = await properties.Single(p => p.Key == "Uri").Value.GetValueAsync(CancellationToken.None);
        Assert.Equal(await rs.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None), uri);
    }

    [Fact]
    public void ReplicaSetResourceThrowsWhenRequiredParametersAreNull()
    {
        var keyFile = new ParameterResource("keyfile", _ => "key");
        var password = new ParameterResource("password", _ => "pass");

        Assert.Equal("keyFile", Assert.Throws<ArgumentNullException>(
            () => new MongoDBReplicaSetResource("rs0", null!, null, password)).ParamName);
        Assert.Equal("sharedPassword", Assert.Throws<ArgumentNullException>(
            () => new MongoDBReplicaSetResource("rs0", keyFile, null, null!)).ParamName);
    }

    [Fact]
    public void BuildMembersConfigurationAllocatesSequentialIdsForANewReplicaSet()
    {
        var members = BuildMembers(("mongo1:27017", "localhost:27017"), ("mongo2:27017", "localhost:27018"));

        var configuration = MongoDBReplicaSetBuilderExtensions.BuildMembersConfiguration(members, currentMembers: null);

        Assert.Equal([0, 1], configuration.Select(m => m["_id"].AsInt32));
        Assert.Equal(["mongo1:27017", "mongo2:27017"], configuration.Select(m => m["host"].AsString));
        Assert.Equal(["localhost:27017", "localhost:27018"], configuration.Select(m => m["horizons"]["external"].AsString));
    }

    [Fact]
    public void BuildMembersConfigurationPreservesIdsOfExistingMembersWhenAMemberIsAppended()
    {
        var currentMembers = BuildCurrentMembers(("mongo1:27017", 0), ("mongo2:27017", 1));
        var members = BuildMembers(
            ("mongo1:27017", "localhost:27017"),
            ("mongo2:27017", "localhost:27018"),
            ("mongo3:27017", "localhost:27019"));

        var configuration = MongoDBReplicaSetBuilderExtensions.BuildMembersConfiguration(members, currentMembers);

        Assert.Equal([0, 1, 2], configuration.Select(m => m["_id"].AsInt32));
    }

    [Fact]
    public void BuildMembersConfigurationPreservesIdsOfExistingMembersWhenAMemberIsRemoved()
    {
        var currentMembers = BuildCurrentMembers(("mongo1:27017", 0), ("mongo2:27017", 1), ("mongo3:27017", 2));
        // `mongo2` is gone; `mongo1` and `mongo3` must keep the ids they already have.
        var members = BuildMembers(("mongo1:27017", "localhost:27017"), ("mongo3:27017", "localhost:27019"));

        var configuration = MongoDBReplicaSetBuilderExtensions.BuildMembersConfiguration(members, currentMembers);

        Assert.Equal([0, 2], configuration.Select(m => m["_id"].AsInt32));
        Assert.Equal(["mongo1:27017", "mongo3:27017"], configuration.Select(m => m["host"].AsString));
    }

    [Fact]
    public void BuildMembersConfigurationPreservesIdsOfExistingMembersWhenMembersAreReordered()
    {
        var currentMembers = BuildCurrentMembers(("mongo1:27017", 0), ("mongo2:27017", 1), ("mongo3:27017", 2));
        var members = BuildMembers(
            ("mongo3:27017", "localhost:27019"),
            ("mongo1:27017", "localhost:27017"),
            ("mongo2:27017", "localhost:27018"));

        var configuration = MongoDBReplicaSetBuilderExtensions.BuildMembersConfiguration(members, currentMembers);

        Assert.Equal([2, 0, 1], configuration.Select(m => m["_id"].AsInt32));
    }

    [Fact]
    public void BuildMembersConfigurationAllocatesUnusedIdsForNewMembersWhenAMemberIsReplaced()
    {
        var currentMembers = BuildCurrentMembers(("mongo1:27017", 0), ("mongo2:27017", 1));
        // `mongo2` is replaced by `mongo3`, which must not reuse an id that is still taken.
        var members = BuildMembers(("mongo1:27017", "localhost:27017"), ("mongo3:27017", "localhost:27019"));

        var configuration = MongoDBReplicaSetBuilderExtensions.BuildMembersConfiguration(members, currentMembers);

        Assert.Equal([0, 2], configuration.Select(m => m["_id"].AsInt32));
    }

    private static MongoDBReplicaSetBuilderExtensions.MemberHosts[] BuildMembers(params (string Internal, string External)[] members) =>
        [.. members.Select(m => new MongoDBReplicaSetBuilderExtensions.MemberHosts(m.Internal, m.External))];

    private static BsonArray BuildCurrentMembers(params (string Host, int Id)[] members) =>
        [.. members.Select(m => new BsonDocument { ["_id"] = m.Id, ["host"] = m.Host })];

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
