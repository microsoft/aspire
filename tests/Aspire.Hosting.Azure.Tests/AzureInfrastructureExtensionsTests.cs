// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting.Azure.Tests;

public class AzureInfrastructureExtensionsTests
{
    [Fact]
    public async Task AddAzureInfrastructureGeneratesCorrectManifestEntry()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var infrastructure1 = builder.AddAzureInfrastructure("infrastructure1", (infrastructure) =>
        {
            var storage = new StorageAccount("storage")
            {
                Kind = StorageKind.StorageV2,
                Sku = new StorageSku() { Name = StorageSkuName.StandardLrs }
            };
            infrastructure.Add(storage);
            infrastructure.Add(new ProvisioningOutput("storageAccountName", typeof(string)) { Value = storage.Name });
        });

        var manifest = await ManifestUtils.GetManifest(infrastructure1.Resource);
        Assert.Equal("azure.bicep.v0", manifest["type"]?.ToString());
        Assert.Equal("infrastructure1.module.bicep", manifest["path"]?.ToString());
    }

    [Fact]
    public async Task AssignParameterPopulatesParametersEverywhere()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:skuName"] = "Standard_ZRS";

        var skuName = builder.AddParameter("skuName");

        AzureResourceInfrastructure? moduleInfrastructure = null;
        var infrastructure1 = builder.AddAzureInfrastructure("infrastructure1", (infrastructure) =>
        {
            var storage = new StorageAccount("storage")
            {
                Kind = StorageKind.StorageV2,
                Sku = new StorageSku() { Name = skuName.AsProvisioningParameter(infrastructure) }
            };
            infrastructure.Add(storage);
            moduleInfrastructure = infrastructure;
        });

        var manifest = await ManifestUtils.GetManifest(infrastructure1.Resource);

        Assert.NotNull(moduleInfrastructure);
        var infrastructureParameters = moduleInfrastructure.GetParameters().DistinctBy(x => x.BicepIdentifier);
        var infrastructureParametersLookup = infrastructureParameters.ToDictionary(p => p.BicepIdentifier);
        Assert.True(infrastructureParametersLookup.ContainsKey("skuName"));

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "path": "infrastructure1.module.bicep",
              "params": {
                "skuName": "{skuName.value}"
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task AssignParameterWithSpecifiedNamePopulatesParametersEverywhere()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:skuName"] = "Standard_ZRS";

        var skuName = builder.AddParameter("skuName");

        AzureResourceInfrastructure? moduleInfrastructure = null;
        var infrastructure1 = builder.AddAzureInfrastructure("infrastructure1", (infrastructure) =>
        {
            var storage = new StorageAccount("storage")
            {
                Kind = StorageKind.StorageV2,
                Sku = new StorageSku() { Name = skuName.AsProvisioningParameter(infrastructure, parameterName: "sku") }
            };
            infrastructure.Add(storage);
            moduleInfrastructure = infrastructure;
        });

        var manifest = await ManifestUtils.GetManifest(infrastructure1.Resource);

        Assert.NotNull(moduleInfrastructure);
        var infrastructureParameters = moduleInfrastructure.GetParameters().DistinctBy(x => x.BicepIdentifier);
        var infrastructureParametersLookup = infrastructureParameters.ToDictionary(p => p.BicepIdentifier);
        Assert.True(infrastructureParametersLookup.ContainsKey("sku"));

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "path": "infrastructure1.module.bicep",
              "params": {
                "sku": "{skuName.value}"
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }
}
