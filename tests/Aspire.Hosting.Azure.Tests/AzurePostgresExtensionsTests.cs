// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzurePostgresExtensionsTests
{
    [Theory]
    // [InlineData(true, true)] this scenario is covered in RoleAssignmentTests.PostgresSupport. The output doesn't match the pattern here because the role assignment isn't generated
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AddAzurePostgresFlexibleServer(bool publishMode, bool useAcaInfrastructure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(publishMode ? DistributedApplicationOperation.Publish : DistributedApplicationOperation.Run);

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");

        if (useAcaInfrastructure)
        {
            builder.AddAzureContainerAppEnvironment("env");

            // on ACA infrastructure, if there are no references to the postgres resource,
            // then there won't be any roles created. So add a reference here.
            builder.AddContainer("api", "myimage")
                .WithReference(postgres);
        }

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(postgres.Resource, skipPreparer: true);

        var postgresRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "postgres-data-roles");
        var (postgresRolesManifest, postgresRolesBicep) = await AzureManifestUtils.GetManifestWithBicep(postgresRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(postgresRolesManifest.ToString(), "json")
              .AppendContentAsFile(postgresRolesBicep, "bicep");
              
    }

    [Fact]
    public async Task AddAzurePostgresFlexibleServer_WithPasswordAuthentication_NoKeyVaultWithContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzurePostgresFlexibleServer("pg").WithPasswordAuthentication().RunAsContainer();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        Assert.Empty(model.Resources.OfType<AzureKeyVaultResource>());
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, true, "mykeyvault")]
    [InlineData(true, false, null)]
    [InlineData(true, false, "mykeyvault")]
    [InlineData(false, true, null)]
    [InlineData(false, true, "mykeyvault")]
    [InlineData(false, false, null)]
    [InlineData(false, false, "mykeyvault")]
    public async Task AddAzurePostgresWithPasswordAuth(bool specifyUserName, bool specifyPassword, string? kvName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var userName = specifyUserName ? builder.AddParameter("user") : null;
        var password = specifyPassword ? builder.AddParameter("password") : null;

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");

        if (kvName is null)
        {
            kvName = "postgres-data-kv";
            postgres.WithPasswordAuthentication(userName, password);
        }
        else
        {
            var keyVault = builder.AddAzureKeyVault(kvName);
            postgres.WithPasswordAuthentication(keyVault, userName, password);
        }

        postgres.AddDatabase("db1", "db1Name");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(postgres.Resource);

        await Verify(bicep, extension: "bicep")
                .AppendContentAsFile(manifest.ToString(), "json");
            
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAzurePostgresFlexibleServerRunAsContainerProducesCorrectConnectionString(bool addDbBeforeRunAsContainer)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");

        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db1 = null!;
        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db2 = null!;
        if (addDbBeforeRunAsContainer)
        {
            db1 = postgres.AddDatabase("db1");
            db2 = postgres.AddDatabase("db2", "db2Name");

        }
        postgres.RunAsContainer(c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455));
        });

        if (!addDbBeforeRunAsContainer)
        {
            db1 = postgres.AddDatabase("db1");
            db2 = postgres.AddDatabase("db2", "db2Name");
        }

        Assert.True(postgres.Resource.IsContainer(), "The resource should now be a container resource.");
        Assert.StartsWith("Host=localhost;Port=12455;Username=postgres;Password=", await postgres.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));

        var db1ConnectionString = await db1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Host=localhost;Port=12455;Username=postgres;Password=", db1ConnectionString);
        Assert.EndsWith("Database=db1", db1ConnectionString);

        var db2ConnectionString = await db2.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Host=localhost;Port=12455;Username=postgres;Password=", db2ConnectionString);
        Assert.EndsWith("Database=db2Name", db2ConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAzurePostgresFlexibleServerRunAsContainerProducesCorrectUserNameAndPasswordAndHost(bool addDbBeforeRunAsContainer)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");
        var pass = builder.AddParameter("pass", "p@ssw0rd1");
        var user = builder.AddParameter("user", "user1");

        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db1 = null!;
        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db2 = null!;
        if (addDbBeforeRunAsContainer)
        {
            db1 = postgres.AddDatabase("db1");
            db2 = postgres.AddDatabase("db2", "db2Name");
        }

        IResourceBuilder<PostgresServerResource>? innerPostgres = null;
        postgres.RunAsContainer(configureContainer: c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455))
                .WithHostPort(12455)
                .WithPassword(pass)
                .WithUserName(user);
            innerPostgres = c;
        });

        if (!addDbBeforeRunAsContainer)
        {
            db1 = postgres.AddDatabase("db1");
            db2 = postgres.AddDatabase("db2", "db2Name");
        }

        Assert.NotNull(innerPostgres);

        var endpoint = Assert.Single(innerPostgres.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5432, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(12455, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        Assert.True(postgres.Resource.IsContainer(), "The resource should now be a container resource.");
        Assert.Equal("Host=localhost;Port=12455;Username=user1;Password=p@ssw0rd1", await postgres.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));

        // Test the new reference properties
        Assert.NotNull(postgres.Resource.Host);
        Assert.Equal("localhost", await postgres.Resource.Host.GetValueAsync(CancellationToken.None));

        Assert.NotNull(postgres.Resource.Port);
        Assert.Equal("12455", await postgres.Resource.Port.GetValueAsync(CancellationToken.None));

        Assert.NotNull(postgres.Resource.HostName);
        Assert.Equal("localhost:12455", await postgres.Resource.HostName.GetValueAsync(CancellationToken.None));

        Assert.NotNull(postgres.Resource.UserName);
        Assert.Equal("user1", await postgres.Resource.UserName.GetValueAsync(CancellationToken.None));

        Assert.NotNull(postgres.Resource.Password);
        Assert.Equal("p@ssw0rd1", await postgres.Resource.Password.GetValueAsync(CancellationToken.None));

        var db1ConnectionString = await db1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.Equal("Host=localhost;Port=12455;Username=user1;Password=p@ssw0rd1;Database=db1", db1ConnectionString);

        var db2ConnectionString = await db2.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.Equal("Host=localhost;Port=12455;Username=user1;Password=p@ssw0rd1;Database=db2Name", db2ConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithPasswordAuthenticationBeforeAfterRunAsContainer(bool before)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var usr = builder.AddParameter("usr", "user");
        var pwd = builder.AddParameter("pwd", "p@ssw0rd1", secret: true);

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");

        if (before)
        {
            postgres.WithPasswordAuthentication(usr, pwd);
        }

        postgres.RunAsContainer(c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455));
        });

        if (!before)
        {
            postgres.WithPasswordAuthentication(usr, pwd);
        }

        var db1 = postgres.AddDatabase("db1");
        var db2 = postgres.AddDatabase("db2", "db2Name");

        Assert.Equal("Host=localhost;Port=12455;Username=user;Password=p@ssw0rd1", await postgres.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));
        Assert.Equal("Host=localhost;Port=12455;Username=user;Password=p@ssw0rd1;Database=db1", await db1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));
        Assert.Equal("Host=localhost;Port=12455;Username=user;Password=p@ssw0rd1;Database=db2Name", await db2.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RunAsContainerAppliesAnnotationsCorrectly(bool annotationsBefore, bool addDatabaseBefore)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");
        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource>? db = null;

        if (addDatabaseBefore)
        {
            db = postgres.AddDatabase("db1");
        }

        if (annotationsBefore)
        {
            postgres.WithAnnotation(new Dummy1Annotation());
            db?.WithAnnotation(new Dummy1Annotation());
        }

        postgres.RunAsContainer(c =>
        {
            c.WithAnnotation(new Dummy2Annotation());
        });

        if (!addDatabaseBefore)
        {
            db = postgres.AddDatabase("db1");

            if (annotationsBefore)
            {
                // need to add the annotation here in this case becuase it has to be added after the DB is created
                db!.WithAnnotation(new Dummy1Annotation());
            }
        }

        if (!annotationsBefore)
        {
            postgres.WithAnnotation(new Dummy1Annotation());
            db!.WithAnnotation(new Dummy1Annotation());
        }

        var postgresResourceInModel = builder.Resources.Single(r => r.Name == "postgres-data");
        var dbResourceInModel = builder.Resources.Single(r => r.Name == "db1");

        Assert.True(postgresResourceInModel.TryGetAnnotationsOfType<Dummy1Annotation>(out var postgresAnnotations1));
        Assert.Single(postgresAnnotations1);

        Assert.True(postgresResourceInModel.TryGetAnnotationsOfType<Dummy2Annotation>(out var postgresAnnotations2));
        Assert.Single(postgresAnnotations2);

        Assert.True(dbResourceInModel.TryGetAnnotationsOfType<Dummy1Annotation>(out var dbAnnotations));
        Assert.Single(dbAnnotations);
    }

    [Fact]
    public async Task AddAzurePostgresFlexibleServerWithAutoGeneratedParametersProducesCorrectValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-test")
                              .WithPasswordAuthentication(); // Auto-generates username and password

        postgres.RunAsContainer(c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5432));
        });

        // Even with auto-generated parameters, UserName and Password should be available
        Assert.NotNull(postgres.Resource.UserName);
        Assert.NotNull(postgres.Resource.Password);
        Assert.NotNull(postgres.Resource.HostName);
        
        // Validate the values can be resolved
        var hostValue = await postgres.Resource.HostName.GetValueAsync(CancellationToken.None);
        Assert.Equal("localhost:5432", hostValue);
        
        var userValue = await postgres.Resource.UserName.GetValueAsync(CancellationToken.None);
        Assert.NotNull(userValue);
        Assert.NotEmpty(userValue);
        
        var passwordValue = await postgres.Resource.Password.GetValueAsync(CancellationToken.None);
        Assert.NotNull(passwordValue);
        Assert.NotEmpty(passwordValue);
    }

    [Fact]
    public void AddAzurePostgresFlexibleServerAADModeUserNameAndPasswordAreNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");
        postgres.AddDatabase("db1");

        // In AAD mode (default, no password authentication), UserName and Password should be null
        Assert.Null(postgres.Resource.UserName);
        Assert.Null(postgres.Resource.Password);

        // HostName should still be available and resolve to bicep output
        Assert.NotNull(postgres.Resource.HostName);
        Assert.Equal("{postgres-data.outputs.hostName}", postgres.Resource.HostName.ValueExpression);
    }

    private sealed class Dummy1Annotation : IResourceAnnotation
    {
    }

    private sealed class Dummy2Annotation : IResourceAnnotation
    {
    }

    [Fact]
    public async Task AsAzurePostgresFlexibleServerViaRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["Parameters:usr"] = "user";
        builder.Configuration["Parameters:pwd"] = "password";

        var usr = builder.AddParameter("usr");
        var pwd = builder.AddParameter("pwd", secret: true);

#pragma warning disable CS0618 // Type or member is obsolete
        var postgres = builder.AddPostgres("postgres", usr, pwd).AsAzurePostgresFlexibleServer();
        postgres.AddDatabase("db", "dbName");

        Assert.True(postgres.Resource.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation));
        var azurePostgres = (AzurePostgresResource)connectionStringAnnotation.Resource;
#pragma warning restore CS0618 // Type or member is obsolete

        var manifest = await AzureManifestUtils.GetManifestWithBicep(postgres.Resource);

        // Setup to verify that connection strings is acquired via resource connectionstring redirct.
        Assert.NotNull(azurePostgres);
        azurePostgres.SecretOutputs["connectionString"] = "myconnectionstring";
        Assert.Equal("myconnectionstring", await postgres.Resource.GetConnectionStringAsync(default));

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{postgres.secretOutputs.connectionString}",
              "path": "postgres.module.bicep",
              "params": {
                "administratorLogin": "{usr.value}",
                "administratorLoginPassword": "{pwd.value}",
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AsAzurePostgresFlexibleServerViaPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Configuration["Parameters:usr"] = "user";
        builder.Configuration["Parameters:pwd"] = "password";

        var usr = builder.AddParameter("usr");
        var pwd = builder.AddParameter("pwd", secret: true);

#pragma warning disable CS0618 // Type or member is obsolete
        var postgres = builder.AddPostgres("postgres", usr, pwd).AsAzurePostgresFlexibleServer();
        postgres.AddDatabase("db", "dbName");

        Assert.True(postgres.Resource.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation));
        var azurePostgres = (AzurePostgresResource)connectionStringAnnotation.Resource;
#pragma warning restore CS0618 // Type or member is obsolete

        var manifest = await AzureManifestUtils.GetManifestWithBicep(postgres.Resource);

        // Setup to verify that connection strings is acquired via resource connectionstring redirct.
        Assert.NotNull(azurePostgres);
        azurePostgres.SecretOutputs["connectionString"] = "myconnectionstring";
        Assert.Equal("myconnectionstring", await postgres.Resource.GetConnectionStringAsync(default));

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{postgres.secretOutputs.connectionString}",
              "path": "postgres.module.bicep",
              "params": {
                "administratorLogin": "{usr.value}",
                "administratorLoginPassword": "{pwd.value}",
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task PublishAsAzurePostgresFlexibleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["Parameters:usr"] = "user";
        builder.Configuration["Parameters:pwd"] = "password";

        var usr = builder.AddParameter("usr");
        var pwd = builder.AddParameter("pwd", secret: true);

#pragma warning disable CS0618 // Type or member is obsolete
        var postgres = builder.AddPostgres("postgres", usr, pwd).PublishAsAzurePostgresFlexibleServer();
        postgres.AddDatabase("db");
#pragma warning restore CS0618 // Type or member is obsolete

        var manifest = await AzureManifestUtils.GetManifestWithBicep(postgres.Resource);

        // Verify that when PublishAs variant is used, connection string acquisition
        // still uses the local endpoint.
        postgres.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1234));
        var expectedConnectionString = $"Host=localhost;Port=1234;Username=user;Password=password";
        Assert.Equal(expectedConnectionString, await postgres.Resource.GetConnectionStringAsync());

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{postgres.secretOutputs.connectionString}",
              "path": "postgres.module.bicep",
              "params": {
                "administratorLogin": "{usr.value}",
                "administratorLoginPassword": "{pwd.value}",
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());
    }

    [Fact]
    public async Task PublishAsAzurePostgresFlexibleServerNoUserPassParams()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

#pragma warning disable CS0618 // Type or member is obsolete
        var postgres = builder.AddPostgres("postgres1")
            .PublishAsAzurePostgresFlexibleServer(); // Because of InternalsVisibleTo

        var manifest = await ManifestUtils.GetManifest(postgres.Resource);
        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{postgres1.secretOutputs.connectionString}",
              "path": "postgres1.module.bicep",
              "params": {
                "administratorLogin": "{postgres1-username.value}",
                "administratorLoginPassword": "{postgres1-password.value}",
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        var param = builder.AddParameter("param");

        postgres = builder.AddPostgres("postgres2", userName: param)
            .PublishAsAzurePostgresFlexibleServer();

        manifest = await ManifestUtils.GetManifest(postgres.Resource);
        expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{postgres2.secretOutputs.connectionString}",
              "path": "postgres2.module.bicep",
              "params": {
                "administratorLogin": "{param.value}",
                "administratorLoginPassword": "{postgres2-password.value}",
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        postgres = builder.AddPostgres("postgres3", password: param)
            .PublishAsAzurePostgresFlexibleServer();
#pragma warning restore CS0618 // Type or member is obsolete

        manifest = await ManifestUtils.GetManifest(postgres.Resource);
        expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{postgres3.secretOutputs.connectionString}",
              "path": "postgres3.module.bicep",
              "params": {
                "administratorLogin": "{postgres3-username.value}",
                "administratorLoginPassword": "{param.value}",
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzurePostgresFlexibleServerResource()
    {
        // Arrange
        var postgresResource = new AzurePostgresFlexibleServerResource("test-postgres", _ => { });
        var infrastructure = new AzureResourceInfrastructure(postgresResource, "test-postgres");

        // Act - Call AddAsExistingResource twice
        var firstResult = postgresResource.AddAsExistingResource(infrastructure);
        var secondResult = postgresResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzurePostgresFlexibleServerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-postgres-name");
        var existingResourceGroup = builder.AddParameter("existing-postgres-rg");

        var postgres = builder.AddAzurePostgresFlexibleServer("test-postgres")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = postgres.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public void WithPasswordAuthentication_SetsSecretOwner(string? kvName)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddAzurePostgresFlexibleServer("postgres-data");

        // Act
        if (kvName is null)
        {
            postgres.WithPasswordAuthentication();
        }
        else
        {
            var keyVault = builder.AddAzureKeyVault(kvName);
            postgres.WithPasswordAuthentication(keyVault);
        }

        // Assert - Verify that the SecretOwner is set to the Postgres resource
        Assert.NotNull(postgres.Resource.ConnectionStringSecretOutput);
        Assert.Same(postgres.Resource, postgres.Resource.ConnectionStringSecretOutput.SecretOwner);
        
        // Also verify that References includes both the KeyVault and the Postgres resource
        var references = ((IValueWithReferences)postgres.Resource.ConnectionStringSecretOutput).References.ToList();
        Assert.Contains(postgres.Resource, references);
        Assert.Contains(postgres.Resource.ConnectionStringSecretOutput.Resource, references);
    }
}
