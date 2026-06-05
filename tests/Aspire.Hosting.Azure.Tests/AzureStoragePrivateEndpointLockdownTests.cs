// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Utils;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting.Azure.Tests;

public class AzureStoragePrivateEndpointLockdownTests
{
    [Fact]
    public async Task AddAzureStorage_WithPrivateEndpoint_CanOverrideWithConfigureInfrastructure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage")
            .ConfigureInfrastructure(infra =>
            {
                var storageAccount = infra.GetProvisionableResources().OfType<StorageAccount>().Single();
                storageAccount.PublicNetworkAccess = StoragePublicNetworkAccess.Enabled;
                storageAccount.NetworkRuleSet!.DefaultAction = StorageNetworkDefaultAction.Allow;
            });
        var blobs = storage.AddBlobs("blobs");

        subnet.AddPrivateEndpoint(blobs);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        // Override should result in Allow/Enabled
        Assert.Contains("defaultAction: 'Allow'", manifest.BicepText);
        Assert.Contains("publicNetworkAccess: 'Enabled'", manifest.BicepText);
    }

    [Fact]
    public async Task AddAzureStorage_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");
        var queues = storage.AddQueues("queues");

        subnet.AddPrivateEndpoint(blobs);
        subnet.AddPrivateEndpoint(queues);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureStorage_WithTablePrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var tables = storage.AddTables("tables");

        subnet.AddPrivateEndpoint(tables);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureStorage_WithDataLakePrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage").ConfigureInfrastructure(infra =>
        {
            // Need to enable HNS for DataLake
            var storageAccount = infra.GetProvisionableResources().OfType<StorageAccount>().Single();
            storageAccount.IsHnsEnabled = true;
        });
        var dataLake = storage.AddDataLake("datalake");

        subnet.AddPrivateEndpoint(dataLake);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }
}
