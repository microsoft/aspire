// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureNatGatewayExtensionsTests
{
    [Fact]
    public void AddNatGateway_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var natGw = builder.AddNatGateway("mynat");

        Assert.NotNull(natGw);
        Assert.Equal("mynat", natGw.Resource.Name);
        Assert.IsType<AzureNatGatewayResource>(natGw.Resource);
    }

    [Fact]
    public void AddNatGateway_InRunMode_DoesNotAddToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var natGw = builder.AddNatGateway("mynat");

        Assert.DoesNotContain(natGw.Resource, builder.Resources);
    }

    [Fact]
    public async Task AddNatGateway_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddNatGateway("mynat");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(builder.Resources.OfType<AzureNatGatewayResource>().Single());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNatGateway_WithExplicitPublicIP_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var pip = builder.AddPublicIPAddress("mypip");
        builder.AddNatGateway("mynat")
            .WithPublicIPAddress(pip);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(builder.Resources.OfType<AzureNatGatewayResource>().Single());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void WithNatGateway_SetsSubnetNatGateway()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var natGw = builder.AddNatGateway("mynat");
        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", "10.0.1.0/24")
            .WithNatGateway(natGw);

        Assert.Same(natGw.Resource, subnet.Resource.NatGateway);
    }

    [Fact]
    public void AddPublicIPAddress_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var pip = builder.AddPublicIPAddress("mypip");

        Assert.NotNull(pip);
        Assert.Equal("mypip", pip.Resource.Name);
        Assert.IsType<AzurePublicIPAddressResource>(pip.Resource);
    }

    [Fact]
    public async Task AddPublicIPAddress_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddPublicIPAddress("mypip");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(builder.Resources.OfType<AzurePublicIPAddressResource>().Single());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddPublicIPAddress_HasIpAddressOutputReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var pip = builder.AddPublicIPAddress("mypip");

        Assert.Equal("ipAddress", pip.Resource.IpAddress.Name);
    }
}
