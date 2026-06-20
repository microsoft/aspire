// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;

#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

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
        Assert.Equal(
            "Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value}{cond-sqlserver-bindings-tcp-tlsenabled-bbec657b.connectionString}",
            connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task SqlServerConnectionStringWithNoCertificate()
    {
        var connectionStrings = await GetConnectionStringsForScenarioAsync(
            builder => builder.AddSqlServer("sqlserver").WithoutHttpsCertificate());

        Assert.True(connectionStrings.ConnectionStringBuilder.TrustServerCertificate);
        Assert.True(string.IsNullOrEmpty(connectionStrings.ConnectionStringBuilder.HostNameInCertificate));

        Assert.Contains("trustServerCertificate=true", connectionStrings.JdbcConnectionString);
        Assert.DoesNotContain("encrypt=", connectionStrings.JdbcConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerConnectionStringWithPreV6DeveloperCertificate()
    {
        using var certificate = CreateCertificate(devCertVersion: 5);

        var connectionStrings = await GetConnectionStringsForScenarioAsync(
            builder =>
            {
                builder.Services.AddSingleton<IDeveloperCertificateService>(
                    new TestDeveloperCertificateService([certificate], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: true));

                return builder.AddSqlServer("sqlserver").WithHttpsDeveloperCertificate();
            });

        Assert.True(connectionStrings.ConnectionStringBuilder.TrustServerCertificate);
        Assert.True(string.IsNullOrEmpty(connectionStrings.ConnectionStringBuilder.HostNameInCertificate));

        Assert.Contains(";trustServerCertificate=true", connectionStrings.JdbcConnectionString);
        Assert.DoesNotContain(";encrypt=", connectionStrings.JdbcConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerConnectionStringWithModernDeveloperCertificate()
    {
        using var certificate = CreateCertificate(devCertVersion: 6);

        var connectionStrings = await GetConnectionStringsForScenarioAsync(
            builder =>
            {
                builder.Services.AddSingleton<IDeveloperCertificateService>(
                    new TestDeveloperCertificateService([certificate], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: true));

                return builder.AddSqlServer("sqlserver").WithHttpsDeveloperCertificate();
            });

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, connectionStrings.ConnectionStringBuilder.Encrypt);
        Assert.False(connectionStrings.ConnectionStringBuilder.TrustServerCertificate);
        Assert.True(string.IsNullOrEmpty(connectionStrings.ConnectionStringBuilder.HostNameInCertificate));

        Assert.Contains("encrypt=true", connectionStrings.JdbcConnectionString);
        Assert.DoesNotContain("trustServerCertificate=true", connectionStrings.JdbcConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerConnectionStringWithImplicitDeveloperCertificate()
    {
        using var certificate = CreateCertificate(devCertVersion: 6);

        var connectionStrings = await GetConnectionStringsForScenarioAsync(
            builder =>
            {
                builder.Services.AddSingleton<IDeveloperCertificateService>(
                    new TestDeveloperCertificateService([certificate], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: true));

                return builder.AddSqlServer("sqlserver");
            });

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, connectionStrings.ConnectionStringBuilder.Encrypt);
        Assert.False(connectionStrings.ConnectionStringBuilder.TrustServerCertificate);
        Assert.True(string.IsNullOrEmpty(connectionStrings.ConnectionStringBuilder.HostNameInCertificate));
        Assert.Contains(";encrypt=true", connectionStrings.JdbcConnectionString);
        Assert.DoesNotContain(";trustServerCertificate=true", connectionStrings.JdbcConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerConnectionStringWithImplicitPreV6DeveloperCertificateUsesTrustServerCertificate()
    {
        using var certificate = CreateCertificate(devCertVersion: 2);

        var connectionStrings = await GetConnectionStringsForScenarioAsync(
            builder =>
            {
                builder.Services.AddSingleton<IDeveloperCertificateService>(
                    new TestDeveloperCertificateService([certificate], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: true));

                return builder.AddSqlServer("sqlserver");
            });

        Assert.True(connectionStrings.ConnectionStringBuilder.TrustServerCertificate);
        Assert.True(string.IsNullOrEmpty(connectionStrings.ConnectionStringBuilder.HostNameInCertificate));
        Assert.Contains(";trustServerCertificate=true", connectionStrings.JdbcConnectionString);
        Assert.DoesNotContain(";encrypt=", connectionStrings.JdbcConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerConnectionStringWithCustomCertificate()
    {
        using var certificate = CreateCertificate();

        var connectionStrings = await GetConnectionStringsForScenarioAsync(
            builder => builder.AddSqlServer("sqlserver").WithHttpsCertificate(certificate));

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, connectionStrings.ConnectionStringBuilder.Encrypt);
        Assert.False(connectionStrings.ConnectionStringBuilder.TrustServerCertificate);
        Assert.True(string.IsNullOrEmpty(connectionStrings.ConnectionStringBuilder.HostNameInCertificate));
        
        Assert.Contains("encrypt=true", connectionStrings.JdbcConnectionString);
        Assert.DoesNotContain("trustServerCertificate=true", connectionStrings.JdbcConnectionString, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(
            "Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value}{cond-sqlserver-bindings-tcp-tlsenabled-bbec657b.connectionString}",
            connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    private static async Task<(SqlConnectionStringBuilder ConnectionStringBuilder, string JdbcConnectionString)> GetConnectionStringsForScenarioAsync(Func<IDistributedApplicationBuilder, IResourceBuilder<SqlServerServerResource>> configure)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var sqlServer = configure(appBuilder)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        await eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel), default);

        var connectionString = await sqlServer.Resource.GetConnectionStringAsync(default);
        Assert.NotNull(connectionString);
        Assert.DoesNotContain(";;", connectionString);

        var jdbcConnectionString = await sqlServer.Resource.JdbcConnectionString.GetValueAsync(default);
        Assert.NotNull(jdbcConnectionString);
        Assert.DoesNotContain(";;", jdbcConnectionString);

        return (
            new SqlConnectionStringBuilder(connectionString),
            jdbcConnectionString);
    }

    private static X509Certificate2 CreateCertificate(byte? devCertVersion = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=localhost"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (devCertVersion is byte version)
        {
            request.CertificateExtensions.Add(new X509Extension(
                new Oid("1.3.6.1.4.1.311.84.1.1", "ASP.NET Core HTTPS development certificate"),
                [version],
                critical: false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
