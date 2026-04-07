// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzurePrivateEndpointExtensionsTests
{
    [Fact]
    public void AddPrivateEndpoint_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var pe = subnet.AddPrivateEndpoint(blobs);

        Assert.NotNull(pe);
        Assert.Equal("pesubnet-blobs-pe", pe.Resource.Name);
        Assert.IsType<AzurePrivateEndpointResource>(pe.Resource);
        Assert.Same(subnet.Resource, pe.Resource.Subnet);
        Assert.Same(blobs.Resource, pe.Resource.Target);
    }

    [Fact]
    public void AddPrivateEndpoint_AddsAnnotationToParentStorage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        // Before adding PE, no annotation
        Assert.Empty(storage.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>());

        subnet.AddPrivateEndpoint(blobs);

        // After adding PE, annotation should be on parent storage
        var annotation = storage.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
    }

    [Fact]
    public void AddPrivateEndpoint_ForQueues_AddsAnnotationToParentStorage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var queues = storage.AddQueues("queues");

        subnet.AddPrivateEndpoint(queues);

        var annotation = storage.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
    }

    [Fact]
    public async Task AddPrivateEndpoint_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var pe = subnet.AddPrivateEndpoint(blobs);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(pe.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddPrivateEndpoint_ForQueues_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var queues = storage.AddQueues("queues");

        var pe = subnet.AddPrivateEndpoint(queues);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(pe.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddPrivateEndpoint_InRunMode_DoesNotAddToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var pe = subnet.AddPrivateEndpoint(blobs);

        // In run mode, the PE resource should not be added to the builder's resources
        Assert.DoesNotContain(pe.Resource, builder.Resources);
    }

    [Fact]
    public void AzureBlobStorageResource_ImplementsIAzurePrivateEndpointTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        Assert.IsAssignableFrom<IAzurePrivateEndpointTarget>(blobs.Resource);

        var target = (IAzurePrivateEndpointTarget)blobs.Resource;
        Assert.Equal(["blob"], target.GetPrivateLinkGroupIds());
        Assert.Equal("privatelink.blob.core.windows.net", target.GetPrivateDnsZoneName());
    }

    [Fact]
    public void AzureQueueStorageResource_ImplementsIAzurePrivateEndpointTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storage = builder.AddAzureStorage("storage");
        var queues = storage.AddQueues("queues");

        Assert.IsAssignableFrom<IAzurePrivateEndpointTarget>(queues.Resource);

        var target = (IAzurePrivateEndpointTarget)queues.Resource;
        Assert.Equal(["queue"], target.GetPrivateLinkGroupIds());
        Assert.Equal("privatelink.queue.core.windows.net", target.GetPrivateDnsZoneName());
    }

    [Fact]
    public async Task AddPrivateEndpoint_ReusesDnsZone_ForSameZoneName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");

        // Two storage accounts with blob endpoints (same DNS zone name)
        var storage1 = builder.AddAzureStorage("storage1");
        var blobs1 = storage1.AddBlobs("blobs1");

        var storage2 = builder.AddAzureStorage("storage2");
        var blobs2 = storage2.AddBlobs("blobs2");

        // Create two private endpoints for the same DNS zone type
        var pe1 = subnet.AddPrivateEndpoint(blobs1);
        var pe2 = subnet.AddPrivateEndpoint(blobs2);

        // Should only have one DNS Zone resource
        var dnsZones = builder.Resources.OfType<AzurePrivateDnsZoneResource>().ToList();
        Assert.Single(dnsZones);
        Assert.Equal("privatelink.blob.core.windows.net", dnsZones[0].ZoneName);

        // Should only have one VNet Link
        Assert.Single(dnsZones[0].VNetLinks);
        var vnetLinks = builder.Resources.OfType<AzurePrivateDnsZoneVNetLinkResource>().ToList();
        Assert.Single(vnetLinks);

        // Verify the bicep for DNS Zone, VNet Link, and both PEs
        var (_, dnsZoneBicep) = await AzureManifestUtils.GetManifestWithBicep(dnsZones[0]);
        var (_, pe1Bicep) = await AzureManifestUtils.GetManifestWithBicep(pe1.Resource);
        var (_, pe2Bicep) = await AzureManifestUtils.GetManifestWithBicep(pe2.Resource);

        await Verify(dnsZoneBicep, extension: "bicep")
            .AppendContentAsFile(pe1Bicep, "bicep", "pe1")
            .AppendContentAsFile(pe2Bicep, "bicep", "pe2");
    }

    [Fact]
    public void AddPrivateEndpoint_CreatesSeparateDnsZones_ForDifferentZoneNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");
        var queues = storage.AddQueues("queues");

        // Create two private endpoints for different DNS zone types
        subnet.AddPrivateEndpoint(blobs);
        subnet.AddPrivateEndpoint(queues);

        // Should have two DNS Zone resources
        var dnsZones = builder.Resources.OfType<AzurePrivateDnsZoneResource>().ToList();
        Assert.Equal(2, dnsZones.Count);
        Assert.Contains(dnsZones, z => z.ZoneName == "privatelink.blob.core.windows.net");
        Assert.Contains(dnsZones, z => z.ZoneName == "privatelink.queue.core.windows.net");

        // Each DNS Zone should have one VNet Link
        Assert.All(dnsZones, z => Assert.Single(z.VNetLinks));
    }
}
