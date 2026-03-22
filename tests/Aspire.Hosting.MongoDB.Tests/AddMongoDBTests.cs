// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Equal("mongodb://admin:{mongodb-password.value}@{mongodb.bindings.tcp.host}:{mongodb.bindings.tcp.port}/?authSource=admin&authMechanism=SCRAM-SHA-256", serverResource.ConnectionStringExpression.ValueExpression);
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"mongodb://admin:{dbResource.Parent.PasswordParameter?.Value}@localhost:27017/mydatabase?authSource=admin&authMechanism=SCRAM-SHA-256", connectionString);
#pragma warning restore CS0618 // Type or member is obsolete
        Assert.Equal("mongodb://admin:{mongodb-password.value}@{mongodb.bindings.tcp.host}:{mongodb.bindings.tcp.port}/mydatabase?authSource=admin&authMechanism=SCRAM-SHA-256", connectionStringResource.ConnectionStringExpression.ValueExpression);
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
        Assert.Equal(expectedManifest, mongoManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "mongodb://admin:{mongo-password-uri-encoded.value}@{mongo.bindings.tcp.host}:{mongo.bindings.tcp.port}/mydb?authSource=admin\u0026authMechanism=SCRAM-SHA-256"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
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

        Assert.Equal("mongodb://admin:{mongo1-password.value}@{mongo1.bindings.tcp.host}:{mongo1.bindings.tcp.port}/customers1?authSource=admin&authMechanism=SCRAM-SHA-256", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("mongodb://admin:{mongo1-password.value}@{mongo1.bindings.tcp.host}:{mongo1.bindings.tcp.port}/customers2?authSource=admin&authMechanism=SCRAM-SHA-256", db2.Resource.ConnectionStringExpression.ValueExpression);
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

        Assert.Equal("mongodb://admin:{mongo1-password.value}@{mongo1.bindings.tcp.host}:{mongo1.bindings.tcp.port}/imports?authSource=admin&authMechanism=SCRAM-SHA-256", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("mongodb://admin:{mongo2-password.value}@{mongo2.bindings.tcp.host}:{mongo2.bindings.tcp.port}/imports?authSource=admin&authMechanism=SCRAM-SHA-256", db2.Resource.ConnectionStringExpression.ValueExpression);
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
}
