// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

#pragma warning disable ASPIRECERTIFICATES001

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
        Assert.Equal("Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value}{cond-sqlserver-bindings-tcp-tlsenabled-bbec657b.connectionString}", connectionStringResource.ConnectionStringExpression.ValueExpression);
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
              "connectionString": "Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={sqlserver-password.value}{cond-sqlserver-bindings-tcp-tlsenabled-bbec657b.connectionString}",
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
              "connectionString": "Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value}{cond-sqlserver-bindings-tcp-tlsenabled-bbec657b.connectionString}",
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
        Assert.Equal("Server={sqlserver.bindings.tcp.host},{sqlserver.bindings.tcp.port};User ID=sa;Password={pass.value}{cond-sqlserver-bindings-tcp-tlsenabled-bbec657b.connectionString}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task SqlServerWithCertificateHasCorrectConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var cert = CreateTestCertificate();

        var pass = builder.AddParameter("pass", "p@ssw0rd1");
        var sqlServer = builder.AddSqlServer("sqlserver", pass)
            .WithHttpsCertificate(cert)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.True(sqlServer.Resource.PrimaryEndpoint.TlsEnabled);

        var connectionString = await sqlServer.Resource.GetConnectionStringAsync(default);
        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=p@ssw0rd1;Encrypt=true", connectionString);

        var jdbcConnectionString = await sqlServer.Resource.JdbcConnectionString.GetValueAsync(default);
        Assert.Equal("jdbc:sqlserver://localhost:1433;encrypt=true", jdbcConnectionString);
    }

    [Fact]
    public async Task SqlServerWithoutCertificateHasCorrectConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var pass = builder.AddParameter("pass", "p@ssw0rd1");
        var sqlServer = builder.AddSqlServer("sqlserver", pass)
            .WithoutHttpsCertificate()
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.False(sqlServer.Resource.PrimaryEndpoint.TlsEnabled);

        var connectionString = await sqlServer.Resource.GetConnectionStringAsync(default);
        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true", connectionString);

        var jdbcConnectionString = await sqlServer.Resource.JdbcConnectionString.GetValueAsync(default);
        Assert.Equal("jdbc:sqlserver://localhost:1433;trustServerCertificate=true", jdbcConnectionString);
    }

    [Fact]
    public async Task SqlServerWritesMssqlConfWhenCertificateIsAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sqlServer = builder.AddSqlServer("sqlserver");

        var annotation = Assert.Single(sqlServer.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        Assert.Equal("/var/opt/mssql", annotation.DestinationPath);

        var context = new ContainerFileSystemCallbackContext
        {
            Model = sqlServer.Resource,
            Services = new ServiceCollection().BuildServiceProvider(),
            HttpsCertificateContext = new ContainerFileSystemCallbackHttpsCertificateContext
            {
                CertificatePath = ReferenceExpression.Create($"/certs/cert.pem"),
                KeyPath = ReferenceExpression.Create($"/certs/key.pem"),
                CertificateWithKeyPath = ReferenceExpression.Create($"/certs/combined.pem"),
                PfxPath = ReferenceExpression.Create($"/certs/cert.pfx"),
            },
        };

        var entries = await annotation.Callback(context, default);
        var file = Assert.IsType<ContainerFile>(Assert.Single(entries));

        Assert.Equal("mssql.conf", file.Name);
        Assert.Contains("tlscert = /certs/cert.pem", file.Contents);
        Assert.Contains("tlskey = /certs/key.pem", file.Contents);
        Assert.Contains("forceencryption = 1", file.Contents);
    }

    [Fact]
    public async Task SqlServerWritesNoMssqlConfFilesWhenNoCertificateIsAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sqlServer = builder.AddSqlServer("sqlserver");

        var annotation = Assert.Single(sqlServer.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());

        var context = new ContainerFileSystemCallbackContext
        {
            Model = sqlServer.Resource,
            Services = new ServiceCollection().BuildServiceProvider(),
            HttpsCertificateContext = null,
        };

        var entries = await annotation.Callback(context, default);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task SqlServerDoesNotEnableTlsWhenDeveloperCertificateIsTooOldForLoopbackAddress()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var oldDevCert = CreateFakeDeveloperCertificate(version: 5);

        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            [oldDevCert], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: true));

        var sqlServer = builder.AddSqlServer("sqlserver")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.False(sqlServer.Resource.PrimaryEndpoint.TlsEnabled);

        var connectionString = await sqlServer.Resource.GetConnectionStringAsync(default);
        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=" + await sqlServer.Resource.PasswordParameter.GetValueAsync(default) + ";TrustServerCertificate=true", connectionString);
    }

    [Fact]
    public async Task SqlServerEnablesTlsWhenDeveloperCertificateSupportsLoopbackAddress()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var devCert = CreateFakeDeveloperCertificate(version: 6);

        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            [devCert], supportsContainerTrust: true, trustCertificate: true, tlsTerminate: true));

        var sqlServer = builder.AddSqlServer("sqlserver")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        Assert.True(sqlServer.Resource.PrimaryEndpoint.TlsEnabled);

        var connectionString = await sqlServer.Resource.GetConnectionStringAsync(default);
        Assert.Equal("Server=127.0.0.1,1433;User ID=sa;Password=" + await sqlServer.Resource.PasswordParameter.GetValueAsync(default) + ";Encrypt=true", connectionString);
    }

    private static X509Certificate2 CreateFakeDeveloperCertificate(byte version)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509Extension(
            new AsnEncodedData(new Oid("1.3.6.1.4.1.311.84.1.1", "ASP.NET Core HTTPS development certificate"), [version]),
            critical: false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
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
