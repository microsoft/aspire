// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKustoExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task AddAzureKustoCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kusto = builder.AddAzureKustoCluster("kusto");
        kusto.AddReadWriteDatabase("testdb");
        kusto.Resource.Outputs["clusterUri"] = "https://kusto-cluster.eastus.kusto.windows.net";
        Assert.Equal("https://kusto-cluster.eastus.kusto.windows.net", await kusto.Resource.ConnectionStringExpression.GetValueAsync(default));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, kusto.Resource);

        var connectionStringResource = (IResourceWithConnectionString)kusto.Resource;
        Assert.Equal("https://kusto-cluster.eastus.kusto.windows.net", await connectionStringResource.GetConnectionStringAsync());

        var kustoRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "kusto-roles");
        var kustoRolesManifest = await GetManifestWithBicep(kustoRoles, skipPreparer: true);

        await Verify(manifest.BicepText, extension: "bicep")
            .AppendContentAsFile(kustoRolesManifest.BicepText, "bicep");
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureKustoClusterResource()
    {
        // Arrange
        var kustoResource = new AzureKustoClusterResource("test-kusto", _ => { });
        var infrastructure = new AzureResourceInfrastructure(kustoResource, "test-kusto");

        // Act - Call AddAsExistingResource twice
        var firstResult = kustoResource.AddAsExistingResource(infrastructure);
        var secondResult = kustoResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureKustoClusterResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var existingName = builder.AddParameter("existing-kusto-name");
        var existingResourceGroup = builder.AddParameter("existing-kusto-rg");

        var kusto = builder.AddAzureKustoCluster("test-kusto")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = kusto.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void AddAzureKustoCluster_ShouldAddResourceToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resourceBuilder = builder.AddAzureKustoCluster("myKusto");
        Assert.NotNull(resourceBuilder);
        var resource = Assert.Single(builder.Resources.OfType<AzureKustoClusterResource>());
        Assert.Equal("myKusto", resource.Name);
    }

    [Fact]
    public void AddAzureKustoCluster_RunAsEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();
        
        // Verify the original resource has the emulator annotation
        Assert.True(kusto.Resource.TryGetLastAnnotation<EmulatorResourceAnnotation>(out _));
        
        // Verify the connection string expression exists
        var connectionStringExpr = kusto.Resource.ConnectionStringExpression;
        Assert.NotNull(connectionStringExpr);
        
        // Verify there's an emulator resource in the resources (check that the resource is running as emulator)
        Assert.True(kusto.Resource.IsEmulator);
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
