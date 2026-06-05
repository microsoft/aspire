// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.Provisioning.CosmosDB;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureCosmosDBExtensionsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(null)]
    [InlineData(8081)]
    [InlineData(9007)]
    public void AddAzureCosmosDBWithEmulatorGetsExpectedPort(int? port = null)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos");

        cosmos.RunAsEmulator(container =>
        {
            container.WithGatewayPort(port);
        });

        var endpointAnnotation = cosmos.Resource.Annotations.OfType<EndpointAnnotation>().FirstOrDefault();
        Assert.NotNull(endpointAnnotation);

        var actualPort = endpointAnnotation.Port;
        Assert.Equal(port, actualPort);
    }

    [Theory]
    [InlineData("2.3.97-preview")]
    [InlineData("1.0.7")]
    public void AddAzureCosmosDBWithEmulatorGetsExpectedImageTag(string imageTag)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos");

        cosmos.RunAsEmulator(container =>
        {
            container.WithImageTag(imageTag);
        });

        var containerImageAnnotation = cosmos.Resource.Annotations.OfType<ContainerImageAnnotation>().FirstOrDefault();
        Assert.NotNull(containerImageAnnotation);

        var actualTag = containerImageAnnotation.Tag;
        Assert.Equal(imageTag ?? "latest", actualTag);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(12)]
    public async Task AddAzureCosmosDBWithPartitionCountCanOverrideNumberOfPartitions(int partitionCount)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos");

        cosmos.RunAsEmulator(r => r.WithPartitionCount(partitionCount));
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(cosmos.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(partitionCount.ToString(CultureInfo.InvariantCulture), config["AZURE_COSMOS_EMULATOR_PARTITION_COUNT"]);
    }

    [Fact]
    public void AddAzureCosmosDBWithDataExplorer()
    {
#pragma warning disable ASPIRECOSMOSDB001 // RunAsPreviewEmulator is experimental
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos");
        cosmos.RunAsPreviewEmulator(e => e.WithDataExplorer());

        var endpoint = cosmos.GetEndpoint("data-explorer");
        Assert.NotNull(endpoint);
        Assert.Equal(1234, endpoint.TargetPort);

        // WithDataExplorer doesn't work against the non-preview emulator
        var cosmos2 = builder.AddAzureCosmosDB("cosmos2");
        Assert.Throws<NotSupportedException>(() => cosmos2.RunAsEmulator(e => e.WithDataExplorer()));
#pragma warning restore ASPIRECOSMOSDB001 // RunAsPreviewEmulator is experimental
    }

    [Fact]
    public void AzureCosmosDBHasCorrectConnectionStrings_ForAccountEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos");
        var db1 = cosmos.AddCosmosDatabase("db1");
        var container1 = db1.AddContainer("container1", "id");

        Assert.Equal("{cosmos.outputs.connectionString}", cosmos.Resource.ConnectionStringExpression.ValueExpression);
        // Endpoint-based connection info gets passed as a connection string to
        // support setting the correct properties on child resources.
        Assert.Equal("AccountEndpoint={cosmos.outputs.connectionString};Database=db1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("AccountEndpoint={cosmos.outputs.connectionString};Database=db1;Container=container1", container1.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AzureCosmosDBHasCorrectConnectionStrings(bool useAccessKeyAuth)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos").RunAsEmulator();
        if (useAccessKeyAuth)
        {
            cosmos.WithAccessKeyAuthentication();
        }
        var db1 = cosmos.AddCosmosDatabase("db1");
        var container1 = db1.AddContainer("container1", "id");

        var cosmos1 = builder.AddAzureCosmosDB("cosmos1").RunAsEmulator();
        if (useAccessKeyAuth)
        {
            cosmos1.WithAccessKeyAuthentication();
        }
        var db2 = cosmos1.AddCosmosDatabase("db2", "db");
        var container2 = db2.AddContainer("container2", "id", "container");

        Assert.DoesNotContain(";Database=db1", cosmos.Resource.ConnectionStringExpression.ValueExpression);
        Assert.DoesNotContain(";Database=db1;Container=container1", cosmos.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Contains(";Database=db1", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Contains(";Database=db1;Container=container1", container1.Resource.ConnectionStringExpression.ValueExpression);
        // Validate behavior when resource name and container/database name are different
        Assert.Contains(";Database=db", db2.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Contains(";Database=db;Container=container", container2.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureCosmosDBAppliesAzureFunctionsConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos");
        var db1 = cosmos.AddCosmosDatabase("db1");
        var container1 = db1.AddContainer("container1", "id");

        var target = new Dictionary<string, object>();
        ((IResourceWithAzureFunctionsConfig)cosmos.Resource).ApplyAzureFunctionsConfiguration(target, "cosmos");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Microsoft__Azure__Cosmos__cosmos__AccountEndpoint", k),
            k => Assert.Equal("Aspire__Microsoft__EntityFrameworkCore__Cosmos__cosmos__AccountEndpoint", k),
            k => Assert.Equal("cosmos__accountEndpoint", k));

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)db1.Resource).ApplyAzureFunctionsConfiguration(target, "db1");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Microsoft__Azure__Cosmos__db1__AccountEndpoint", k),
            k => Assert.Equal("Aspire__Microsoft__Azure__Cosmos__db1__DatabaseName", k),
            k => Assert.Equal("Aspire__Microsoft__EntityFrameworkCore__Cosmos__db1__AccountEndpoint", k),
            k => Assert.Equal("Aspire__Microsoft__EntityFrameworkCore__Cosmos__db1__DatabaseName", k),
            k => Assert.Equal("db1__accountEndpoint", k));

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)container1.Resource).ApplyAzureFunctionsConfiguration(target, "container1");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Microsoft__Azure__Cosmos__container1__AccountEndpoint", k),
            k => Assert.Equal("Aspire__Microsoft__Azure__Cosmos__container1__ContainerName", k),
            k => Assert.Equal("Aspire__Microsoft__Azure__Cosmos__container1__DatabaseName", k),
            k => Assert.Equal("Aspire__Microsoft__EntityFrameworkCore__Cosmos__container1__AccountEndpoint", k),
            k => Assert.Equal("Aspire__Microsoft__EntityFrameworkCore__Cosmos__container1__ContainerName", k),
            k => Assert.Equal("Aspire__Microsoft__EntityFrameworkCore__Cosmos__container1__DatabaseName", k),
            k => Assert.Equal("container1__accountEndpoint", k));
    }

    /// <summary>
    /// Test both with and without ACA infrastructure because the role assignments
    /// are handled differently between the two. This ensures that the bicep is generated
    /// consistently regardless of the infrastructure used in RunMode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAzureCosmosDB(bool useAcaInfrastructure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        if (useAcaInfrastructure)
        {
            builder.AddAzureContainerAppEnvironment("env");
        }

        var cosmos = builder.AddAzureCosmosDB("cosmos");

        builder.AddContainer("api", "myimage")
            .WithReference(cosmos);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, cosmos.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
            

        var cosmosRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "cosmos-roles");
        var cosmosRolesManifest = await GetManifestWithBicep(cosmosRoles, skipPreparer: true);
        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param cosmos_outputs_name string

            param principalId string

            resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' existing = {
              name: cosmos_outputs_name
            }

            resource cosmos_roleDefinition 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-08-15' existing = {
              name: '00000000-0000-0000-0000-000000000002'
              parent: cosmos
            }

            resource cosmos_roleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = {
              name: guid(principalId, cosmos_roleDefinition.id, cosmos.id)
              properties: {
                principalId: principalId
                roleDefinitionId: cosmos_roleDefinition.id
                scope: cosmos.id
              }
              parent: cosmos
            }
            """;
        output.WriteLine(cosmosRolesManifest.BicepText);
        Assert.Equal(expectedBicep, cosmosRolesManifest.BicepText);
    }

    [Fact]
    public async Task AddAzureCosmosDatabase_WorksWithAccessKeyAuth_ChildResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .WithAccessKeyAuthentication();
        var database = cosmos.AddCosmosDatabase("db1");
        var container = database.AddContainer("container1", "id");

        builder.AddContainer("api", "myimage")
            .WithReference(cosmos);

        Assert.Equal("{cosmos-kv.secrets.connectionstrings--cosmos}", cosmos.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{cosmos-kv.secrets.connectionstrings--db1}", database.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("{cosmos-kv.secrets.connectionstrings--container1}", container.Resource.ConnectionStringExpression.ValueExpression);

        var manifest = await GetManifestWithBicep(cosmos.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }
    
    [Fact]
    public async Task AddAzureCosmosDBEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos").RunAsEmulator(e =>
        {
            e.WithEndpoint("emulator", e => e.AllocatedEndpoint = new(e, "localhost", 10001));
        });

        Assert.True(cosmos.Resource.IsContainer());

        var csExpr = cosmos.Resource.ConnectionStringExpression;
        var cs = await csExpr.GetValueAsync(CancellationToken.None);

        var prefix = "AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;AccountEndpoint=";
        Assert.Equal(prefix + "https://{cosmos.bindings.emulator.host}:{cosmos.bindings.emulator.port};DisableServerCertificateValidation=True;", csExpr.ValueExpression);
        Assert.Equal(prefix + "https://127.0.0.1:10001;DisableServerCertificateValidation=True;", cs);
        Assert.Equal(cs, await ((IResourceWithConnectionString)cosmos.Resource).GetConnectionStringAsync());
    }

    [Fact]
    public async Task AddAzureCosmosDB_WithAccessKeyAuthentication_NoKeyVaultWithEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzureCosmosDB("cosmos").WithAccessKeyAuthentication().RunAsEmulator();

#pragma warning disable ASPIRECOSMOSDB001
        builder.AddAzureCosmosDB("cosmos2").WithAccessKeyAuthentication().RunAsPreviewEmulator();
#pragma warning restore ASPIRECOSMOSDB001

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        Assert.Empty(model.Resources.OfType<AzureKeyVaultResource>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public async Task AddAzureCosmosDBViaRunMode_WithAccessKeyAuthentication(string? kvName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        IEnumerable<CosmosDBSqlDatabase>? callbackDatabases = null;
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .ConfigureInfrastructure(infrastructure =>
            {
                callbackDatabases = infrastructure.GetProvisionableResources().OfType<CosmosDBSqlDatabase>();
            });

        if (kvName is null)
        {
            kvName = "cosmos-kv";
            cosmos.WithAccessKeyAuthentication();
        }
        else
        {
            cosmos.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        var db = cosmos.AddCosmosDatabase("db", databaseName: "mydatabase");
        db.AddContainer("container", "mypartitionkeypath", containerName: "mycontainer");

        var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kv = model.Resources.OfType<AzureKeyVaultResource>().Single();

        Assert.Equal(kvName, kv.Name);

        var secrets = new Dictionary<string, string>
        {
            ["connectionstrings--cosmos"] = "mycosmosconnectionstring"
        };

        kv.SecretResolver = (secretRef, _) =>
        {
            if (!secrets.TryGetValue(secretRef.SecretName, out var value))
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(value);
        };

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");

        Assert.NotNull(callbackDatabases);
        Assert.Collection(
            callbackDatabases,
            (database) => Assert.Equal("mydatabase", database.Name.Value)
            );

        var connectionStringResource = (IResourceWithConnectionString)cosmos.Resource;

        Assert.Equal("cosmos", cosmos.Resource.Name);
        Assert.Equal("mycosmosconnectionstring", await connectionStringResource.GetConnectionStringAsync());
    }

    [Fact]
    public async Task AddAzureCosmosDBViaRunMode_NoAccessKeyAuthentication()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        IEnumerable<CosmosDBSqlDatabase>? callbackDatabases = null;
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .ConfigureInfrastructure(infrastructure =>
            {
                callbackDatabases = infrastructure.GetProvisionableResources().OfType<CosmosDBSqlDatabase>();
            });
        var db = cosmos.AddCosmosDatabase("mydatabase");
        db.AddContainer("mycontainer", "mypartitionkeypath");

        cosmos.Resource.Outputs["connectionString"] = "mycosmosconnectionstring";

        var manifest = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        var expectedManifest = """
                               {
                                 "type": "azure.bicep.v0",
                                 "connectionString": "{cosmos.outputs.connectionString}",
                                 "path": "cosmos.module.bicep"
                               }
                               """;

        output.WriteLine(manifest.ManifestNode.ToString());
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");

        Assert.NotNull(callbackDatabases);
        Assert.Collection(
            callbackDatabases,
            (database) => Assert.Equal("mydatabase", database.Name.Value)
            );

        var connectionStringResource = (IResourceWithConnectionString)cosmos.Resource;

        Assert.Equal("cosmos", cosmos.Resource.Name);
        Assert.Equal("mycosmosconnectionstring", await connectionStringResource.GetConnectionStringAsync());
    }

    [Theory]
    [InlineData("mykeyvault")]
    [InlineData(null)]
    public async Task AddAzureCosmosDBViaPublishMode_WithAccessKeyAuthentication(string? kvName)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        IEnumerable<CosmosDBSqlDatabase>? callbackDatabases = null;
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .ConfigureInfrastructure(infrastructure =>
            {
                callbackDatabases = infrastructure.GetProvisionableResources().OfType<CosmosDBSqlDatabase>();
            });

        if (kvName is null)
        {
            kvName = "cosmos-kv";
            cosmos.WithAccessKeyAuthentication();
        }
        else
        {
            cosmos.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        var db = cosmos.AddCosmosDatabase("mydatabase");
        db.AddContainer("mycontainer", "mypartitionkeypath");

        var kv = builder.CreateResourceBuilder<AzureKeyVaultResource>(kvName);

        var secrets = new Dictionary<string, string>
        {
            ["connectionstrings--cosmos"] = "mycosmosconnectionstring"
        };

        kv.Resource.SecretResolver = (secretRef, _) =>
        {
            if (!secrets.TryGetValue(secretRef.SecretName, out var value))
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(value);
        };

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");

        Assert.NotNull(callbackDatabases);
        Assert.Collection(
            callbackDatabases,
            (database) => Assert.Equal("mydatabase", database.Name.Value)
            );

        var connectionStringResource = (IResourceWithConnectionString)cosmos.Resource;

        Assert.Equal("cosmos", cosmos.Resource.Name);
        Assert.Equal("mycosmosconnectionstring", await connectionStringResource.GetConnectionStringAsync());
    }

    [Fact]
    public async Task AddAzureCosmosDBViaPublishMode_NoAccessKeyAuthentication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        IEnumerable<CosmosDBSqlDatabase>? callbackDatabases = null;
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .ConfigureInfrastructure(infrastructure =>
            {
                callbackDatabases = infrastructure.GetProvisionableResources().OfType<CosmosDBSqlDatabase>();
            });
        var db = cosmos.AddCosmosDatabase("mydatabase");
        db.AddContainer("mycontainer", "mypartitionkeypath");

        cosmos.Resource.Outputs["connectionString"] = "mycosmosconnectionstring";

        var manifest = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        var expectedManifest = """
                               {
                                 "type": "azure.bicep.v0",
                                 "connectionString": "{cosmos.outputs.connectionString}",
                                 "path": "cosmos.module.bicep"
                               }
                               """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");

        Assert.NotNull(callbackDatabases);
        Assert.Collection(
            callbackDatabases,
            (database) => Assert.Equal("mydatabase", database.Name.Value)
            );

        var connectionStringResource = (IResourceWithConnectionString)cosmos.Resource;

        Assert.Equal("cosmos", cosmos.Resource.Name);
        Assert.Equal("mycosmosconnectionstring", await connectionStringResource.GetConnectionStringAsync());
    }

    [Fact]
    public async Task AddAzureCosmosDBViaPublishMode_WithDefaultAzureSku()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .WithDefaultAzureSku();

        var manifest = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void RunAsEmulatorAppliesEmulatorResourceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos")
                           .RunAsEmulator();

        // Verify that the EmulatorResourceAnnotation is applied
        Assert.True(cosmos.Resource.IsEmulator());
        Assert.Contains(cosmos.Resource.Annotations, a => a is EmulatorResourceAnnotation);
    }

    [Fact]
    public void RunAsPreviewEmulatorAppliesEmulatorResourceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
#pragma warning disable ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var cosmos = builder.AddAzureCosmosDB("cosmos")
                           .RunAsPreviewEmulator();
#pragma warning restore ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        // Verify that the EmulatorResourceAnnotation is applied
        Assert.True(cosmos.Resource.IsEmulator());
        Assert.Contains(cosmos.Resource.Annotations, a => a is EmulatorResourceAnnotation);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureCosmosDBResource()
    {
        // Arrange
        var cosmosDBResource = new AzureCosmosDBResource("test-cosmosdb", _ => { });
        var infrastructure = new AzureResourceInfrastructure(cosmosDBResource, "test-cosmosdb");

        // Act - Call AddAsExistingResource twice
        var firstResult = cosmosDBResource.AddAsExistingResource(infrastructure);
        var secondResult = cosmosDBResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureCosmosDBResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-cosmosdb-name");
        var existingResourceGroup = builder.AddParameter("existing-cosmosdb-rg");

        var cosmosdb = builder.AddAzureCosmosDB("test-cosmosdb")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = cosmosdb.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public void WithAccessKeyAuthentication_SetsSecretOwner(string? kvName)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmos-data");

        // Act
        if (kvName is null)
        {
            cosmos.WithAccessKeyAuthentication();
        }
        else
        {
            cosmos.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        // Assert - Verify that the SecretOwner is set to the Cosmos resources
        Assert.NotNull(cosmos.Resource.ConnectionStringSecretOutput);
        Assert.Same(cosmos.Resource, cosmos.Resource.ConnectionStringSecretOutput.SecretOwner);

        Assert.NotNull(cosmos.Resource.PrimaryAccessKeySecretOutput);
        Assert.Same(cosmos.Resource, cosmos.Resource.PrimaryAccessKeySecretOutput.SecretOwner);

        // Also verify that References includes both the KeyVault and the Cosmos resource
        var references = ((IValueWithReferences)cosmos.Resource.ConnectionStringSecretOutput).References.ToList();
        Assert.Contains(cosmos.Resource, references);
        Assert.Contains(cosmos.Resource.ConnectionStringSecretOutput.Resource, references);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
