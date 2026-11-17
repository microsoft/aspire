// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using HealthChecks.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Net.Sockets;

namespace Aspire.Hosting.SqlServer.Tests;

public class AddSqlServerTests
{
    [Fact]
    public void AddSqlServerAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var sql = appBuilder.AddSqlServer("sql");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", sql.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddSqlServerDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sql = appBuilder.AddSqlServer("sql");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", sql.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddSqlServerContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddSqlServer("sqlserver");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<SqlServerServerResource>());
        Assert.Equal("sqlserver", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1433, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(SqlServerContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(SqlServerContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(SqlServerContainerImageTags.Registry, containerAnnotation.Registry);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("ACCEPT_EULA", env.Key);
                Assert.Equal("Y", env.Value);
            },
            env =>
            {
                Assert.Equal("MSSQL_SA_PASSWORD", env.Key);
                Assert.NotNull(env.Value);
                Assert.True(env.Value.Length >= 8);
            });
    }

    [Fact]
    public async Task SqlServerCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "p@ssw0rd1");
        appBuilder
            .AddSqlServer("sqlserver", pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<SqlServerServerResource>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);

        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true", connectionString);
        Assert.Equal("Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value};TrustServerCertificate=true", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task SqlServerDatabaseCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "p@ssw0rd1");
        appBuilder
            .AddSqlServer("sqlserver", pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433))
            .AddDatabase("mydb");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var sqlResource = Assert.Single(appModel.Resources.OfType<SqlServerDatabaseResource>());
        var connectionStringResource = (IResourceWithConnectionString)sqlResource;
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true;Initial Catalog=mydb", connectionString);
        Assert.Equal("{sqlserver.connectionString};Initial Catalog=mydb", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddSqlServer("sqlserver");
        var db = sqlServer.AddDatabase("db");

        var serverManifest = await ManifestUtils.GetManifest(sqlServer.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={sqlserver-password.value};TrustServerCertificate=true",
              "image": "{{SqlServerContainerImageTags.Registry}}/{{SqlServerContainerImageTags.Image}}:{{SqlServerContainerImageTags.Tag}}",
              "env": {
                "ACCEPT_EULA": "Y",
                "MSSQL_SA_PASSWORD": "{sqlserver-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 1433
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "{sqlserver.connectionString};Initial Catalog=db"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithPasswordParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var pass = builder.AddParameter("pass");

        var sqlServer = builder.AddSqlServer("sqlserver", pass);
        var serverManifest = await ManifestUtils.GetManifest(sqlServer.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value};TrustServerCertificate=true",
              "image": "{{SqlServerContainerImageTags.Registry}}/{{SqlServerContainerImageTags.Image}}:{{SqlServerContainerImageTags.Tag}}",
              "env": {
                "ACCEPT_EULA": "Y",
                "MSSQL_SA_PASSWORD": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 1433
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

        var db = builder.AddSqlServer("sqlserver1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddSqlServer("sqlserver1")
            .AddDatabase("db");

        var db = builder.AddSqlServer("sqlserver2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sqlserver1 = builder.AddSqlServer("sqlserver1");

        var db1 = sqlserver1.AddDatabase("db1", "customers1");
        var db2 = sqlserver1.AddDatabase("db2", "customers2");

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        Assert.Equal("{sqlserver1.connectionString};Initial Catalog=customers1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{sqlserver1.connectionString};Initial Catalog=customers2", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db1 = builder.AddSqlServer("sqlserver1")
            .AddDatabase("db1", "imports");

        var db2 = builder.AddSqlServer("sqlserver2")
            .AddDatabase("db2", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        Assert.Equal("{sqlserver1.connectionString};Initial Catalog=imports", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{sqlserver2.connectionString};Initial Catalog=imports", db2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void VerifySqlServerServerResourceWithHostPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddSqlServer("sqlserver1")
            .WithHostPort(1000);

        var resource = Assert.Single(builder.Resources.OfType<SqlServerServerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1000, endpoint.Port);
    }

    [Fact]
    public async Task VerifySqlServerServerResourceWithPassword()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "p@ssw0rd1");
        appBuilder
            .AddSqlServer("sqlserver")
            .WithPassword(pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<SqlServerServerResource>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);
        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true", connectionString);
        Assert.Equal("Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value};TrustServerCertificate=true", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void HealthCheckConnectionStringDisablesPoolBlockingPeriod()
    {
        var connectionString = SqlServerBuilderExtensions.CreateHealthCheckConnectionString("Server=127.0.0.1,1433;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true;Initial Catalog=mydb");

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(PoolBlockingPeriod.NeverBlock, builder.PoolBlockingPeriod);
        Assert.Equal("127.0.0.1,1433", builder.DataSource);
        Assert.Equal("sa", builder.UserID);
        Assert.Equal("p@ssw0rd1", builder.Password);
        Assert.True(builder.TrustServerCertificate);
        Assert.Equal("mydb", builder.InitialCatalog);
    }

    [Fact]
    public async Task HealthChecksAreCreatedAfterConnectionStringIsAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var pass = builder.AddParameter("pass", "p@ssw0rd1");
        var sqlServer = builder.AddSqlServer("sqlserver", pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));
        var db = sqlServer.AddDatabase("mydb");

        using var app = builder.Build();

        var registrations = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var serverRegistration = Assert.Single(registrations, r => r.Name == "sqlserver_check");
        var dbRegistration = Assert.Single(registrations, r => r.Name == "mydb_check");

        Assert.Equal("Connection string is unavailable", Assert.Throws<InvalidOperationException>(() => serverRegistration.Factory(app.Services)).Message);
        Assert.Equal("Connection string is unavailable", Assert.Throws<InvalidOperationException>(() => dbRegistration.Factory(app.Services)).Message);

        await builder.Eventing.PublishAsync(new ConnectionStringAvailableEvent(sqlServer.Resource, app.Services));
        await builder.Eventing.PublishAsync(new ConnectionStringAvailableEvent(db.Resource, app.Services));

        Assert.IsType<SqlServerHealthCheck>(serverRegistration.Factory(app.Services));
        Assert.IsType<SqlServerHealthCheck>(dbRegistration.Factory(app.Services));
    }
}
