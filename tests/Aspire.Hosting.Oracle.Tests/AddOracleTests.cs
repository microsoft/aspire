// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Oracle.Tests;

public class AddOracleTests
{
    [Fact]
    public void AddOracleAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var orcl = appBuilder.AddOracle("orcl");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", orcl.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddOracleDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var orcl = appBuilder.AddOracle("orcl");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", orcl.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddOracleWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddOracle("orcl");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("orcl", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(OracleContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(OracleContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(OracleContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1521, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("ORACLE_PWD", env.Key);
                Assert.False(string.IsNullOrEmpty(env.Value));
            });
    }

    [Fact]
    public async Task AddOracleAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");
        appBuilder.AddOracle("orcl", pass, 1234);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("orcl", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(OracleContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(OracleContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(OracleContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1521, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(1234, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("ORACLE_PWD", env.Key);
                Assert.Equal("pass", env.Value);
            });
    }

    [Fact]
    public async Task OracleCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddOracle("orcl")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);

        Assert.Equal("user id=system;password={orcl-password.value};data source={orcl.bindings.tcp.host}:{orcl.bindings.tcp.port}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.StartsWith("user id=system;password=", connectionString);
        Assert.EndsWith(";data source=localhost:2000", connectionString);
    }

    [Fact]
    public async Task OracleCreatesConnectionStringWithDatabase()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddOracle("orcl")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
            .AddDatabase("db");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var oracleResource = Assert.Single(appModel.Resources.OfType<OracleDatabaseServerResource>());
        var oracleConnectionStringResource = (IResourceWithConnectionString)oracleResource;
        var oracleConnectionString = oracleConnectionStringResource.GetConnectionStringAsync();
        var oracleDatabaseResource = Assert.Single(appModel.Resources.OfType<OracleDatabaseResource>());
        var oracleDatabaseConnectionStringResource = (IResourceWithConnectionString)oracleDatabaseResource;
        var dbConnectionString = await oracleDatabaseConnectionStringResource.GetConnectionStringAsync();

        Assert.Equal("FREEPDB1", oracleDatabaseResource.DatabaseName);
        Assert.Equal("{orcl.connectionString}/FREEPDB1", oracleDatabaseConnectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.Equal(oracleConnectionString + "/FREEPDB1", dbConnectionString);
    }

    [Fact]
    public async Task AddDatabaseToOracleDatabaseAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");
        appBuilder.AddOracle("oracle", pass, 1234).AddDatabase("db");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var containerResource = Assert.Single(containerResources);
        Assert.Equal("oracle", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(OracleContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(OracleContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(OracleContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1521, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(1234, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("ORACLE_PWD", env.Key);
                Assert.Equal("pass", env.Value);
            });
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var oracleServer = builder.AddOracle("oracle");
        var db = oracleServer.AddDatabase("db");

        var serverManifest = await ManifestUtils.GetManifest(oracleServer.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "user id=system;password={oracle-password.value};data source={oracle.bindings.tcp.host}:{oracle.bindings.tcp.port}",
              "image": "{{OracleContainerImageTags.Registry}}/{{OracleContainerImageTags.Image}}:{{OracleContainerImageTags.Tag}}",
              "env": {
                "ORACLE_PWD": "{oracle-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 1521
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "{oracle.connectionString}/FREEPDB1"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithPasswordParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pass = builder.AddParameter("pass");

        var oracleServer = builder.AddOracle("oracle", pass);
        var serverManifest = await ManifestUtils.GetManifest(oracleServer.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "user id=system;password={pass.value};data source={oracle.bindings.tcp.host}:{oracle.bindings.tcp.port}",
              "image": "{{OracleContainerImageTags.Registry}}/{{OracleContainerImageTags.Image}}:{{OracleContainerImageTags.Tag}}",
              "env": {
                "ORACLE_PWD": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 1521
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db = builder.AddOracle("oracle1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddOracle("oracle1")
            .AddDatabase("db");

        var db = builder.AddOracle("oracle2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var oracle1 = builder.AddOracle("oracle1");

        var db1 = oracle1.AddDatabase("db1", "customers1");
        var db2 = oracle1.AddDatabase("db2", "customers2");

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        Assert.Equal("{oracle1.connectionString}/customers1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{oracle1.connectionString}/customers2", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AddDatabaseDefaultsToOracleFreePluggableDatabase()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db = builder.AddOracle("oracle")
            .AddDatabase("appdb");

        Assert.Equal("appdb", db.Resource.Name);
        Assert.Equal("FREEPDB1", db.Resource.DatabaseName);
        Assert.Equal("{oracle.connectionString}/FREEPDB1", db.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db1 = builder.AddOracle("oracle1")
            .AddDatabase("db1", "imports");

        var db2 = builder.AddOracle("oracle2")
            .AddDatabase("db2", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        Assert.Equal("{oracle1.connectionString}/imports", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{oracle2.connectionString}/imports", db2.Resource.ConnectionStringExpression.ValueExpression);
    }
}
