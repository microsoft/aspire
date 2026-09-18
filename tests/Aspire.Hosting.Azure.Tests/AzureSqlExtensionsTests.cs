// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSqlExtensionsTests
{
    [Theory]
    // [InlineData(true, true)] this scenario is covered in RoleAssignmentTests.SqlSupport. The output doesn't match the pattern here because the role assignment isn't generated
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AddAzureSqlServer(bool publishMode, bool useAcaInfrastructure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(publishMode ? DistributedApplicationOperation.Publish : DistributedApplicationOperation.Run);

        var sql = builder.AddAzureSqlServer("sql");

        // database name same as the aspire resource name, free tier 
        sql.AddDatabase("db1");

        // set the database name, free tier 
        sql.AddDatabase("db2", "db2Name");

        // do not set any sku, use whatever is the default for Azure at that time
        sql.AddDatabase("db3", "db3Name").WithDefaultAzureSku();

        if (useAcaInfrastructure)
        {
            builder.AddAzureContainerAppEnvironment("env");

            // on ACA infrastructure, if there are no references to the resource,
            // then there won't be any roles created. So add a reference here.
            builder.AddContainer("api", "myimage")
                .WithReference(sql);
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(sql.Resource, skipPreparer: true);

        var expectedManifest = $$"""
            {
              "type": "azure.bicep.v0",
              "connectionString": "Server=tcp:{sql.outputs.sqlServerFqdn},1433;Encrypt=True;Authentication=\u0022Active Directory Default\u0022",
              "path": "sql.module.bicep"
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString(), ignoreLineEndingDifferences: true);

        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAzureSqlServerRunAsContainerProducesCorrectConnectionString(bool addDbBeforeRunAsContainer)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sql = builder.AddAzureSqlServer("sql");

        IResourceBuilder<AzureSqlDatabaseResource> db1 = null!;
        IResourceBuilder<AzureSqlDatabaseResource> db2 = null!;
        IResourceBuilder<AzureSqlDatabaseResource> db3 = null!;

        if (addDbBeforeRunAsContainer)
        {
            db1 = sql.AddDatabase("db1");
            db2 = sql.AddDatabase("db2", "db2Name");
            db3 = sql.AddDatabase("db3", "db3Name").WithDefaultAzureSku();
        }
        sql.RunAsContainer(c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455));
        });

        if (!addDbBeforeRunAsContainer)
        {
            db1 = sql.AddDatabase("db1");
            db2 = sql.AddDatabase("db2", "db2Name");
            db3 = sql.AddDatabase("db3", "db3Name").WithDefaultAzureSku();
        }

        Assert.True(sql.Resource.IsContainer(), "The resource should now be a container resource.");
        var serverConnectionString = await sql.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=", serverConnectionString);
        Assert.EndsWith(";TrustServerCertificate=true", serverConnectionString);

        var db1ConnectionString = await db1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=", db1ConnectionString);
        Assert.EndsWith(";TrustServerCertificate=true;Database=db1", db1ConnectionString);

        var db2ConnectionString = await db2.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=", db2ConnectionString);
        Assert.EndsWith(";TrustServerCertificate=true;Database=db2Name", db2ConnectionString);

        var db3ConnectionString = await db3.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=", db3ConnectionString);
        Assert.EndsWith(";TrustServerCertificate=true;Database=db3Name", db3ConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAzureSqlServerRunAsContainerProducesCorrectPasswordAndPort(bool addDbBeforeRunAsContainer)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sql = builder.AddAzureSqlServer("sql");
        var pass = builder.AddParameter("pass", "p@ssw0rd1");

        IResourceBuilder<AzureSqlDatabaseResource> db1 = null!;
        IResourceBuilder<AzureSqlDatabaseResource> db2 = null!;
        IResourceBuilder<AzureSqlDatabaseResource> db3 = null!;

        if (addDbBeforeRunAsContainer)
        {
            db1 = sql.AddDatabase("db1");
            db2 = sql.AddDatabase("db2", "db2Name");
            db3 = sql.AddDatabase("db3", "db3Name").WithDefaultAzureSku();
        }

        IResourceBuilder<SqlServerServerResource>? innerSql = null;
        sql.RunAsContainer(configureContainer: c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455))
                .WithHostPort(12455)
                .WithPassword(pass);
            innerSql = c;
        });

        Assert.NotNull(innerSql);

        if (!addDbBeforeRunAsContainer)
        {
            db1 = sql.AddDatabase("db1");
            db2 = sql.AddDatabase("db2", "db2Name");
            db3 = sql.AddDatabase("db3", "db3Name").WithDefaultAzureSku();
        }

        var endpoint = Assert.Single(innerSql.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(1433, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(12455, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        Assert.True(sql.Resource.IsContainer(), "The resource should now be a container resource.");
        var serverConnectionString = await sql.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.Equal("Server=127.0.0.1,12455;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true", serverConnectionString);

        var db1ConnectionString = await db1.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true;Database=db1", db1ConnectionString);

        var db2ConnectionString = await db2.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true;Database=db2Name", db2ConnectionString);

        var db3ConnectionString = await db3.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.StartsWith("Server=127.0.0.1,12455;User ID=sa;Password=p@ssw0rd1;TrustServerCertificate=true;Database=db3Name", db3ConnectionString);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RunAsContainerAppliesAnnotationsCorrectly(bool annotationsBefore, bool addDatabaseBefore)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sql = builder.AddAzureSqlServer("sql");
        IResourceBuilder<AzureSqlDatabaseResource>? db = null;
        SqlServerServerResource? projection = null;

        if (addDatabaseBefore)
        {
            db = sql.AddDatabase("db1");
        }

        if (annotationsBefore)
        {
            sql.WithAnnotation(new Dummy1Annotation());
            db?.WithAnnotation(new Dummy1Annotation());
        }

        sql.RunAsContainer(c =>
        {
            projection = c.Resource;
            c.WithAnnotation(new Dummy2Annotation());
        });

        if (!addDatabaseBefore)
        {
            db = sql.AddDatabase("db1");

            if (annotationsBefore)
            {
                // need to add the annotation here in this case becuase it has to be added after the DB is created
                db!.WithAnnotation(new Dummy1Annotation());
            }
        }

        if (!annotationsBefore)
        {
            sql.WithAnnotation(new Dummy1Annotation());
            db!.WithAnnotation(new Dummy1Annotation());
        }

        var sqlResourceInModel = builder.Resources.Single(r => r.Name == "sql");
        var dbResourceInModel = builder.Resources.Single(r => r.Name == "db1");

        Assert.True(sqlResourceInModel.TryGetAnnotationsOfType<Dummy1Annotation>(out var sqlAnnotations1));
        Assert.Single(sqlAnnotations1);

        Assert.True(sqlResourceInModel.TryGetAnnotationsOfType<Dummy2Annotation>(out var sqlAnnotations2));
        Assert.Single(sqlAnnotations2);

        Assert.True(dbResourceInModel.TryGetAnnotationsOfType<Dummy1Annotation>(out var dbAnnotations));
        Assert.Single(dbAnnotations);
        Assert.Contains(db!.Resource.Annotations, annotation => annotation is HealthCheckAnnotation);
        ProjectionTestHelpers.AssertProjection(sql, Assert.IsType<AzureSqlServerContainerResource>(projection));
        Assert.Same(db.Resource, dbResourceInModel);
    }   

    [Fact]
    public async Task RunAsContainerReappliesContainerDefaults()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var sql = builder.AddAzureSqlServer("sql").RunAsContainer();
        var container = Assert.IsAssignableFrom<SqlServerServerResource>(sql.Resource.AsContainer());
        Assert.True(container.TryGetContainerImageName(out var defaultImage));

        sql.RunAsContainer(container => container
            .WithImage("custom")
            .WithEnvironment("ACCEPT_EULA", "N")
            .WithEnvironment("MSSQL_SA_PASSWORD", "incorrect"));
        Assert.True(sql.Resource.AsContainer()!.TryGetContainerImageName(out var customImage));
        Assert.Equal("mcr.microsoft.com/custom:latest", customImage);

        sql.RunAsContainer();
        Assert.True(sql.Resource.AsContainer()!.TryGetContainerImageName(out var reappliedImage));
        Assert.Equal(defaultImage, reappliedImage);
        container = Assert.IsAssignableFrom<SqlServerServerResource>(sql.Resource.AsContainer());

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            sql.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance);
        Assert.Equal("Y", environment["ACCEPT_EULA"]);
        Assert.Equal(
            await container.PasswordParameter.GetValueAsync(CancellationToken.None),
            environment["MSSQL_SA_PASSWORD"]);
    }

    private sealed class Dummy1Annotation : IResourceAnnotation
    {
    }

    private sealed class Dummy2Annotation : IResourceAnnotation
    {
    }
    
    [Fact]
    public async Task AsAzureSqlDatabaseViaRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

#pragma warning disable CS0618 // Type or member is obsolete
        var sql = builder.AddSqlServer("sql").AsAzureSqlDatabase();
#pragma warning restore CS0618 // Type or member is obsolete
        sql.AddDatabase("db", "dbName");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(sql.Resource);

        Assert.True(sql.Resource.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation));
        var azureSql = (AzureSqlServerResource)connectionStringAnnotation.Resource;
        azureSql.Outputs["sqlServerFqdn"] = "myserver";

        Assert.Equal("Server=tcp:myserver,1433;Encrypt=True;Authentication=\"Active Directory Default\"", await sql.Resource.GetConnectionStringAsync(default));
        Assert.Equal("Server=tcp:{sql.outputs.sqlServerFqdn},1433;Encrypt=True;Authentication=\"Active Directory Default\"", sql.Resource.ConnectionStringExpression.ValueExpression);

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "Server=tcp:{sql.outputs.sqlServerFqdn},1433;Encrypt=True;Authentication=\u0022Active Directory Default\u0022",
              "path": "sql.module.bicep"
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AsAzureSqlDatabaseViaPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

#pragma warning disable CS0618 // Type or member is obsolete
        var sql = builder.AddSqlServer("sql").AsAzureSqlDatabase();
#pragma warning restore CS0618 // Type or member is obsolete
        sql.AddDatabase("db", "dbName");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(sql.Resource);

        Assert.True(sql.Resource.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation));
        var azureSql = (AzureSqlServerResource)connectionStringAnnotation.Resource;
        azureSql.Outputs["sqlServerFqdn"] = "myserver";

        Assert.Equal("Server=tcp:myserver,1433;Encrypt=True;Authentication=\"Active Directory Default\"", await sql.Resource.GetConnectionStringAsync(default));
        Assert.Equal("Server=tcp:{sql.outputs.sqlServerFqdn},1433;Encrypt=True;Authentication=\"Active Directory Default\"", sql.Resource.ConnectionStringExpression.ValueExpression);

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "Server=tcp:{sql.outputs.sqlServerFqdn},1433;Encrypt=True;Authentication=\u0022Active Directory Default\u0022",
              "path": "sql.module.bicep"
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureSqlServerResource()
    {
        // Arrange
        var sqlResource = new AzureSqlServerResource("test-sql", _ => { });
        var infrastructure = new AzureResourceInfrastructure(sqlResource, "test-sql");

        // Act - Call AddAsExistingResource twice
        var firstResult = sqlResource.AddAsExistingResource(infrastructure);
        var secondResult = sqlResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureSqlServerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-sql-name");
        var existingResourceGroup = builder.AddParameter("existing-sql-rg");

        var sql = builder.AddAzureSqlServer("test-sql")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = sql.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
    
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}