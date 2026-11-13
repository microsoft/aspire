// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREMONGODB001
#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.MongoDB.Tests;

public class MongoDBReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder, bool enableRepl)
    {
        var resource = builder.AddMongoDB("mongo");
        if (enableRepl)
        {
            Assert.Same(resource, resource.WithRepl());
        }

        return resource;
    }

    [Fact]
    public void WithReplRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => MongoDBBuilderExtensions.WithRepl(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(null, "admin", false, false)]
    [InlineData("user with spaces", "user%20with%20spaces", true, false)]
    [InlineData("user@:/", "user%40%3A%2F", false, true)]
    public async Task ReplUsesConfiguredCredentialsAndDirectContainerEndpoint(
        string? username, string encodedUsername, bool tlsEnabled, bool replicaSet)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var user = username is null ? null : builder.AddParameter("username", username);
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var mongo = builder.AddMongoDB("mongo", port: 37017, userName: user, password: password)
            .WithEndpoint("tcp", endpoint => endpoint.TlsEnabled = tlsEnabled);
        if (replicaSet)
        {
            mongo.WithReplicaSet();
        }

        var options = await MongoDBBuilderExtensions.CreateReplOptionsAsync(mongo.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("mongosh (mongo)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "ASPIRE_MONGODB_REPL_CONNECTION_STRING", "container-id",
            "mongosh", "--quiet", "--nodb", "--shell", "--eval", """
            const uri = process.env.ASPIRE_MONGODB_REPL_CONNECTION_STRING;
            const ca = process.env.ASPIRE_MONGODB_REPL_CA_FILE;
            db = new Mongo(ca ? `${uri}&tlsCAFile=${encodeURIComponent(ca)}` : uri).getDB("admin");
            """], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("ASPIRE_MONGODB_REPL_CONNECTION_STRING", variable.Key);
            Assert.Equal($"mongodb://{encodedUsername}:quotes%27%22%20%24%3B%20spaces@localhost:27017/admin?directConnection=true&authSource=admin&authMechanism=SCRAM-SHA-256{(tlsEnabled ? "&tls=true" : "")}",
                variable.Value);
        });
    }

    [Fact]
    public async Task ReplSupportsPasswordlessMongoDB()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var mongo = builder.AddResource(new MongoDBServerResource("mongo"))
            .WithEndpoint(targetPort: 27018, name: "tcp");

        var options = await MongoDBBuilderExtensions.CreateReplOptionsAsync(mongo.Resource, TestContext.Current.CancellationToken);

        Assert.Collection(options.EnvironmentVariables, variable =>
        {
            Assert.Equal("ASPIRE_MONGODB_REPL_CONNECTION_STRING", variable.Key);
            Assert.Equal("mongodb://localhost:27018/admin?directConnection=true", variable.Value);
        });
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("username", "")]
    public async Task ReplRejectsUnavailableCredentials(string username, string password)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var mongo = builder.AddMongoDB("mongo",
            userName: builder.AddParameter("username", username),
            password: builder.AddParameter("password", password, secret: true));

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            MongoDBBuilderExtensions.CreateReplOptionsAsync(mongo.Resource, TestContext.Current.CancellationToken));

        Assert.Equal("The MongoDB REPL credentials are not available.", exception.Message);
    }

    [Theory]
    [InlineData(false, "/usr/lib/ssl/aspire/cert.pem", DistributedApplicationOperation.Run)]
    [InlineData(true, "/usr/lib/ssl/aspire/cert.pem", DistributedApplicationOperation.Run)]
    [InlineData(true, "/custom certificates/cert.pem", DistributedApplicationOperation.Run)]
    [InlineData(true, "/custom certificates/cert.pem", DistributedApplicationOperation.Publish)]
    public async Task ReplInheritsConfiguredCertificateTrustBundle(bool tlsEnabled, string bundlePath, DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var mongo = builder.AddMongoDB("mongo")
            .WithEndpoint("tcp", endpoint => endpoint.TlsEnabled = tlsEnabled);
        var context = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = builder.ExecutionContext,
            Resource = mongo.Resource,
            Arguments = [],
            EnvironmentVariables = [],
            CertificateBundlePath = ReferenceExpression.Create($"{bundlePath}"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"{"/certificates"}"),
            Scope = CertificateTrustScope.Append,
            CancellationToken = TestContext.Current.CancellationToken
        };

        var callback = Assert.Single(mongo.Resource.Annotations.OfType<CertificateTrustConfigurationCallbackAnnotation>());
        await callback.Callback(context);

        if (tlsEnabled)
        {
            Assert.Equal(["--tlsCAFile", context.CertificateBundlePath], context.Arguments);
            Assert.Equal(bundlePath, await context.CertificateBundlePath.GetValueAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Empty(context.Arguments);
        }

        if (tlsEnabled && operation == DistributedApplicationOperation.Run)
        {
            Assert.Collection(context.EnvironmentVariables, variable =>
            {
                Assert.Equal("ASPIRE_MONGODB_REPL_CA_FILE", variable.Key);
                Assert.Same(context.CertificateBundlePath, variable.Value);
            });
        }
        else
        {
            Assert.Empty(context.EnvironmentVariables);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedQuery(bool replicaSet)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var username = builder.AddParameter("username", "repl-user");
        var password = builder.AddParameter("password", "repl-p@ss$word", secret: true);
        var mongo = builder.AddMongoDB("mongo", userName: username, password: password).WithRepl().WithoutHttpsCertificate();
        if (replicaSet)
        {
            mongo.WithReplicaSet();
        }

        await using var app = builder.Build();

        await VerifyReplAsync(app, mongo.Resource, "mongosh (mongo)",
            "admin>", """print('authenticated-' + db.runCommand({ connectionStatus: 1 }).authInfo.authenticatedUsers[0].user)""" + "\r",
            "authenticated-repl-user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/usr/local/share/aspire-repl-ca")]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresFeature(TestFeature.DevCert)]
    public async Task ReplExecutesAuthenticatedQueryWithTls(string? certificateDirectory)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var mongo = builder.AddMongoDB("mongo").WithRepl().WithHttpsDeveloperCertificate();
        if (certificateDirectory is not null)
        {
            mongo.WithContainerCertificatePaths(customCertificatesDestination: certificateDirectory);
        }

        await using var app = builder.Build();

        await VerifyReplAsync(app, mongo.Resource, "mongosh (mongo)",
            "admin>", """print('authenticated-' + db.runCommand({ connectionStatus: 1 }).authInfo.authenticatedUsers[0].user)""" + "\r",
            "authenticated-admin");
        Assert.True(mongo.Resource.TlsEnabled);
    }
}
