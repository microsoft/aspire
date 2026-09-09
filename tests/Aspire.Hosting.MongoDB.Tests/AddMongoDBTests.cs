// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting.MongoDB.Tests;

public class AddMongoDBTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddMongoDBAddsHealthCheckAnnotationToResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var mongo = builder.AddMongoDB("mongodb");
        Assert.Single(mongo.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "mongodb_check");
    }

    [Fact]
    public void AddDatabaseAddsHealthCheckAnnotationToResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var db = builder.AddMongoDB("mongodb").AddDatabase("mydb");
        Assert.Single(db.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "mydb_check");
    }

    [Fact]
    public void AddMongoDBContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddMongoDB("mongodb");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<MongoDBServerResource>());
        Assert.Equal("mongodb", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(27017, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(MongoDBContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(MongoDBContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(MongoDBContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public void AddMongoDBContainerAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddMongoDB("mongodb", 9813);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<MongoDBServerResource>());
        Assert.Equal("mongodb", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(27017, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(9813, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(MongoDBContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(MongoDBContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(MongoDBContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task MongoDBCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddMongoDB("mongodb")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017))
            .AddDatabase("mydatabase");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dbResource = Assert.Single(appModel.Resources.OfType<MongoDBDatabaseResource>());
        var serverResource = dbResource.Parent as IResourceWithConnectionString;
        var connectionStringResource = dbResource as IResourceWithConnectionString;
        Assert.NotNull(connectionStringResource);
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"mongodb://admin:{dbResource.Parent.PasswordParameter?.Value}@localhost:27017/?authSource=admin&authMechanism=SCRAM-SHA-256", await serverResource.GetConnectionStringAsync());
#pragma warning restore CS0618 // Type or member is obsolete
        Assert.Equal("mongodb://admin:{mongodb-password.value}@{mongodb.bindings.tcp.host}:{mongodb.bindings.tcp.port}/?authSource=admin&authMechanism=SCRAM-SHA-256", MongoDBTestHelpers.WithoutTlsFlag(serverResource.ConnectionStringExpression.ValueExpression));
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"mongodb://admin:{dbResource.Parent.PasswordParameter?.Value}@localhost:27017/mydatabase?authSource=admin&authMechanism=SCRAM-SHA-256", connectionString);
#pragma warning restore CS0618 // Type or member is obsolete
        Assert.Equal("mongodb://admin:{mongodb-password.value}@{mongodb.bindings.tcp.host}:{mongodb.bindings.tcp.port}/mydatabase?authSource=admin&authMechanism=SCRAM-SHA-256", MongoDBTestHelpers.WithoutTlsFlag(connectionStringResource.ConnectionStringExpression.ValueExpression));
    }

    [Fact]
    public void WithMongoExpressAddsContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddMongoDB("mongo")
            .WithMongoExpress();

        Assert.Single(builder.Resources.OfType<MongoExpressContainerResource>());
    }

    [Fact]
    public void WithMongoExpressSupportsChangingContainerImageValues()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddMongoDB("mongo").WithMongoExpress(c =>
        {
            c.WithImageRegistry("example.mycompany.com");
            c.WithImage("customongoexpresscontainer");
            c.WithImageTag("someothertag");
        });

        var resource = Assert.Single(builder.Resources.OfType<MongoExpressContainerResource>());
        var containerAnnotation = Assert.Single(resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("example.mycompany.com", containerAnnotation.Registry);
        Assert.Equal("customongoexpresscontainer", containerAnnotation.Image);
        Assert.Equal("someothertag", containerAnnotation.Tag);
    }

    [Fact]
    public void WithMongoExpressSupportsChangingHostPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddMongoDB("mongo").WithMongoExpress(c =>
        {
            c.WithHostPort(1000);
        });

        var resource = Assert.Single(builder.Resources.OfType<MongoExpressContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1000, endpoint.Port);
    }

    [Fact]
    public async Task WithMongoExpressUsesContainerHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddMongoDB("mongo")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 3000))
            .WithMongoExpress();

        var mongoExpress = Assert.Single(builder.Resources.OfType<MongoExpressContainerResource>());

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(mongoExpress, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(env,
            e =>
            {
                Assert.Equal("ME_CONFIG_MONGODB_SERVER", e.Key);
                Assert.Equal("mongo", e.Value);
            },
            e =>
            {
                Assert.Equal("ME_CONFIG_MONGODB_PORT", e.Key);
                Assert.Equal("27017", e.Value);
            },
            e =>
            {
                // NOTE: Only consumed by the image's entrypoint, to wait for the server before starting Mongo Express.
                Assert.Equal("ME_CONFIG_MONGODB_URL", e.Key);
                Assert.Equal("mongodb://mongo:27017", e.Value);
            },
            e =>
            {
                Assert.Equal("ME_CONFIG_BASICAUTH", e.Key);
                Assert.Equal("false", e.Value);
            },
            e =>
            {
                Assert.Equal("ME_CONFIG_MONGODB_ADMINUSERNAME", e.Key);
                Assert.Equal("admin", e.Value);
            },
            e =>
            {
                Assert.Equal("ME_CONFIG_MONGODB_ADMINPASSWORD", e.Key);
                Assert.NotEmpty(e.Value);
            });
    }

    [Fact]
    public void WithMongoExpressOnMultipleResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddMongoDB("mongo").WithMongoExpress();
        builder.AddMongoDB("mongo2").WithMongoExpress();

        Assert.Equal(2, builder.Resources.OfType<MongoExpressContainerResource>().Count());
    }

    [Fact]
    public async Task VerifyManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var mongo = appBuilder.AddMongoDB("mongo");
        var db = mongo.AddDatabase("mydb");

        var mongoManifest = await ManifestUtils.GetManifest(mongo.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "mongodb://admin:{mongo-password-uri-encoded.value}@{mongo.bindings.tcp.host}:{mongo.bindings.tcp.port}/?authSource=admin\u0026authMechanism=SCRAM-SHA-256",
              "image": "{{MongoDBContainerImageTags.Registry}}/{{MongoDBContainerImageTags.Image}}:{{MongoDBContainerImageTags.Tag}}",
              "env": {
                "MONGO_INITDB_ROOT_USERNAME": "admin",
                "MONGO_INITDB_ROOT_PASSWORD": "{mongo-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 27017
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, MongoDBTestHelpers.WithoutTlsFlag(mongoManifest.ToString()));

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "mongodb://admin:{mongo-password-uri-encoded.value}@{mongo.bindings.tcp.host}:{mongo.bindings.tcp.port}/mydb?authSource=admin\u0026authMechanism=SCRAM-SHA-256"
            }
            """;
        Assert.Equal(expectedManifest, MongoDBTestHelpers.WithoutTlsFlag(dbManifest.ToString()));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var db = builder.AddMongoDB("mongo1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddMongoDB("mongo1")
            .AddDatabase("db");

        var db = builder.AddMongoDB("mongo2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mongo1 = builder.AddMongoDB("mongo1");

        var db1 = mongo1.AddDatabase("db1", "customers1");
        var db2 = mongo1.AddDatabase("db2", "customers2");

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        Assert.Equal("mongodb://admin:{mongo1-password.value}@{mongo1.bindings.tcp.host}:{mongo1.bindings.tcp.port}/customers1?authSource=admin&authMechanism=SCRAM-SHA-256", MongoDBTestHelpers.WithoutTlsFlag(db1.Resource.ConnectionStringExpression.ValueExpression));
        Assert.Equal("mongodb://admin:{mongo1-password.value}@{mongo1.bindings.tcp.host}:{mongo1.bindings.tcp.port}/customers2?authSource=admin&authMechanism=SCRAM-SHA-256", MongoDBTestHelpers.WithoutTlsFlag(db2.Resource.ConnectionStringExpression.ValueExpression));
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var db1 = builder.AddMongoDB("mongo1")
            .AddDatabase("db1", "imports");

        var db2 = builder.AddMongoDB("mongo2")
            .AddDatabase("db2", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        Assert.Equal("mongodb://admin:{mongo1-password.value}@{mongo1.bindings.tcp.host}:{mongo1.bindings.tcp.port}/imports?authSource=admin&authMechanism=SCRAM-SHA-256", MongoDBTestHelpers.WithoutTlsFlag(db1.Resource.ConnectionStringExpression.ValueExpression));
        Assert.Equal("mongodb://admin:{mongo2-password.value}@{mongo2.bindings.tcp.host}:{mongo2.bindings.tcp.port}/imports?authSource=admin&authMechanism=SCRAM-SHA-256", MongoDBTestHelpers.WithoutTlsFlag(db2.Resource.ConnectionStringExpression.ValueExpression));
    }

    [Fact]
    public async Task MongoExpressEnvironmentCallbackIsIdempotent()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mongo = appBuilder.AddMongoDB("mongo")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017))
            .WithMongoExpress();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var mongoExpressResource = Assert.Single(appModel.Resources.OfType<MongoExpressContainerResource>());

        // Call GetEnvironmentVariablesAsync multiple times to ensure callbacks are idempotent
        var config1 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(mongoExpressResource);
        var config2 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(mongoExpressResource);

        // Both calls should succeed and return the same values
        Assert.Equal(config1.Count, config2.Count);
        Assert.Contains(config1, kvp => kvp.Key == "ME_CONFIG_MONGODB_SERVER");
        Assert.Contains(config2, kvp => kvp.Key == "ME_CONFIG_MONGODB_SERVER");
        Assert.Equal(
            config1.First(kvp => kvp.Key == "ME_CONFIG_MONGODB_SERVER").Value,
            config2.First(kvp => kvp.Key == "ME_CONFIG_MONGODB_SERVER").Value);
    }

    [Fact]
    public async Task WithBindIpAllAddsBindIpAllArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithBindIpAll();

        Assert.True(mongo1.Resource.HasAnnotationOfType<MongoDBServerBindAllIpAnnotation>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Contains("--bind_ip_all", args);
    }

    [Fact]
    public async Task WithReplicaSetAddsReplSetArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithReplicaSet("test");

        Assert.Equal("test", mongo1.Resource.ReplicaSetName);
        Assert.True(mongo1.Resource.HasAnnotationOfType<MongoDBServerReplicaSetAnnotation>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Contains("--replSet", args);
        Assert.Contains("test", args);
        Assert.Contains("--bind_ip_all", args);
    }

    [Fact]
    public async Task WithKeyFileAddsKeyFileArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithKeyFile(new ParameterResource("test", _ => "test"), "/the/key/file/path");

        Assert.True(mongo1.Resource.HasAnnotationOfType<MongoDBServerKeyFileAnnotation>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Contains("--keyFile", args);
        Assert.Contains("/the/key/file/path", args);
    }

    [Fact]
    public async Task MongoDBWithCertificateEnablesTlsAndAddsCorrectTlsArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        using var certificate = CreateTestCertificate();

        var mongo1 = builder.AddMongoDB("mongo1")
            .WithHttpsCertificate(certificate)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));

        // Before the `BeforeStartEvent` is published, TLS is not yet enabled.
        Assert.False(mongo1.Resource.TlsEnabled);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.True(mongo1.Resource.TlsEnabled);

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Equal("requireTLS", args[args.IndexOf("--tlsMode") + 1]);
        Assert.Contains("--tlsAllowConnectionsWithoutCertificates", args);
        // Weakening certificate validation is not part of the standard TLS setup.
        Assert.DoesNotContain("--tlsAllowInvalidCertificates", args);

        var connectionString = await mongo1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.Contains("tls=true", connectionString);
    }

    [Fact]
    public async Task MongoDBWithoutCertificateDoesNotEnableTls()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mongo1 = builder.AddMongoDB("mongo1")
            .WithoutHttpsCertificate()
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.False(mongo1.Resource.TlsEnabled);

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.DoesNotContain("--tlsMode", args);

        var connectionString = await mongo1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.DoesNotContain("tls=true", connectionString);
    }

    [Fact]
    public async Task MongoExpressIsConfiguredForTlsWhenTheServerUsesIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        using var certificate = CreateTestCertificate();

        var mongoExpress = null as IResourceBuilder<MongoExpressContainerResource>;
        builder.AddMongoDB("mongo")
            .WithHttpsCertificate(certificate)
            .WithMongoExpress(configureContainer: c => mongoExpress = c);

        Assert.NotNull(mongoExpress);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(mongoExpress.Resource);
        Assert.Equal("true", config["ME_CONFIG_MONGODB_SSL"]);
        Assert.Equal("false", config["ME_CONFIG_MONGODB_SSLVALIDATE"]);
    }

    [Fact]
    public async Task MongoExpressIsNotConfiguredForTlsWhenTheServerDoesNotUseIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var mongoExpress = null as IResourceBuilder<MongoExpressContainerResource>;
        builder.AddMongoDB("mongo")
            .WithoutHttpsCertificate()
            .WithMongoExpress(configureContainer: c => mongoExpress = c);

        Assert.NotNull(mongoExpress);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(mongoExpress.Resource);
        Assert.DoesNotContain("ME_CONFIG_MONGODB_SSL", config.Keys);
        Assert.DoesNotContain("ME_CONFIG_MONGODB_SSLVALIDATE", config.Keys);
    }

    [Fact]
    public async Task MongoDBDoesNotEnableTlsWhenTheDeveloperCertificateIsRequestedButUnavailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            [], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: false));

        // NOTE: This is what `WithMember` does for replica set members. Enabling TLS without a certificate to back it
        // would leave `mongod` with a `--tlsMode` it cannot satisfy, and it would refuse to start.
        var mongo1 = builder.AddMongoDB("mongo1")
            .WithHttpsDeveloperCertificate()
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.False(mongo1.Resource.TlsEnabled);

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.DoesNotContain("--tlsMode", args);
    }

    [Fact]
    public async Task WithTlsModeUsesTheConfiguredMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        using var certificate = CreateTestCertificate();

        var mongo1 = builder.AddMongoDB("mongo1")
            .WithHttpsCertificate(certificate)
            .WithTlsMode(MongoDBTlsMode.AllowTls);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Equal("allowTLS", args[args.IndexOf("--tlsMode") + 1]);
    }

    [Fact]
    public async Task WithTlsAllowInvalidCertificatesAddsArgWhenOptedIn()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        using var certificate = CreateTestCertificate();

        var mongo1 = builder.AddMongoDB("mongo1")
            .WithHttpsCertificate(certificate)
            .WithTlsAllowInvalidCertificates();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Contains("--tlsAllowInvalidCertificates", args);
    }

    [Fact]
    public async Task WithBindIpAllIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        // NOTE: `WithReplicaSet` binds all interfaces itself, so this composition is a perfectly reasonable thing to write,
        // and `mongod` refuses to start if it ends up being given the option twice.
        var mongo1 = builder.AddMongoDB("mongo1").WithBindIpAll().WithReplicaSet("rs0").WithBindIpAll();

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Equal(1, args.Count(a => a == "--bind_ip_all"));
    }

    [Fact]
    public async Task WithReplicaSetIsIdempotentForTheSameName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1").WithReplicaSet("rs0").WithReplicaSet("rs0");

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Equal(1, args.Count(a => a == "--replSet"));
        Assert.Equal("rs0", mongo1.Resource.ReplicaSetName);
    }

    [Fact]
    public void WithReplicaSetThrowsForADifferentName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1").WithReplicaSet("rs0");

        var exception = Assert.Throws<InvalidOperationException>(() => mongo1.WithReplicaSet("rs1"));
        Assert.Contains("already configured as a member of the replica set 'rs0'", exception.Message);
    }

    [Fact]
    public async Task WithKeyFileIsIdempotentForTheSameConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var keyValue = new ParameterResource("test", _ => "test");
        var mongo1 = builder.AddMongoDB("mongo1").WithKeyFile(keyValue, "/etc/rs.key").WithKeyFile(keyValue, "/etc/rs.key");

        var args = await ArgumentEvaluator.GetArgumentListAsync(mongo1.Resource);
        Assert.Equal(1, args.Count(a => a == "--keyFile"));
    }

    [Fact]
    public void WithKeyFileThrowsForADifferentConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var mongo1 = builder.AddMongoDB("mongo1").WithKeyFile(new ParameterResource("a", _ => "a"), "/etc/rs.key");

        var exception = Assert.Throws<InvalidOperationException>(
            () => mongo1.WithKeyFile(new ParameterResource("b", _ => "b"), "/etc/other.key"));
        Assert.Contains("already has a key file configured", exception.Message);
    }

    [Fact]
    public void WithTlsModeThrowsInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var mongo1 = builder.AddMongoDB("mongo1");

        Assert.Throws<NotSupportedException>(() => mongo1.WithTlsMode());
    }

    [Fact]
    public void WithTlsAllowInvalidCertificatesThrowsInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var mongo1 = builder.AddMongoDB("mongo1");

        var action = () => mongo1.WithTlsAllowInvalidCertificates();

        Assert.Throws<NotSupportedException>(action);
    }

    [Fact]
    public void WithKeyFileThrowsInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var mongo1 = builder.AddMongoDB("mongo1");

        Assert.Throws<NotSupportedException>(() => mongo1.WithKeyFile(new ParameterResource("test", _ => "test")));
    }

    [Fact]
    public void WithReplicaSetThrowsInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var mongo1 = builder.AddMongoDB("mongo1");

        Assert.Throws<NotSupportedException>(() => mongo1.WithReplicaSet("rs0"));
    }

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
