// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Network;

namespace Aspire.Hosting.Azure.Tests;

public class AzureNetworkSecurityGroupExtensionsTests
{
    [Fact]
    public void AddNetworkSecurityGroup_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsg = builder.AddNetworkSecurityGroup("web-nsg");

        Assert.NotNull(nsg);
        Assert.Equal("web-nsg", nsg.Resource.Name);
        Assert.IsType<AzureNetworkSecurityGroupResource>(nsg.Resource);
    }

    [Fact]
    public void AddNetworkSecurityGroup_InRunMode_DoesNotAddToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nsg = builder.AddNetworkSecurityGroup("web-nsg");

        Assert.DoesNotContain(nsg.Resource, builder.Resources);
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("web-nsg");
        vnet.AddSubnet("web-subnet", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_WithSecurityRules_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("web-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Description = "Allow HTTPS traffic from any source",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                DestinationPortRange = "443"
            })
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "deny-all-inbound",
                Priority = 4096,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Deny,
                Protocol = SecurityRuleProtocol.Asterisk,
                DestinationPortRange = "*"
            });

        vnet.AddSubnet("web-subnet", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg);

        await AzureManifestUtils.VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_WithSecurityRules_GeneratesCorrectNsgModuleBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsg = builder.AddNetworkSecurityGroup("web-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                DestinationPortRange = "443"
            })
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "deny-all-inbound",
                Priority = 4096,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Deny,
                Protocol = SecurityRuleProtocol.Asterisk,
                DestinationPortRange = "*"
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsg.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddSubnet_WithNetworkSecurityGroup_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("web-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                SourceAddressPrefix = "*",
                SourcePortRange = "*",
                DestinationAddressPrefix = "*",
                DestinationPortRange = "443"
            });

        vnet.AddSubnet("web-subnet", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg);

        await AzureManifestUtils.VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_SharedAcrossSubnets_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("shared-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                SourceAddressPrefix = "*",
                SourcePortRange = "*",
                DestinationAddressPrefix = "*",
                DestinationPortRange = "443"
            });

        vnet.AddSubnet("subnet1", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg);
        vnet.AddSubnet("subnet2", "10.0.2.0/24")
            .WithNetworkSecurityGroup(nsg);

        await AzureManifestUtils.VerifyAllAzureBicep(builder);
    }

    [Fact]
    public void WithNetworkSecurityGroup_SetsSubnetNetworkSecurityGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("web-nsg");
        var subnet = vnet.AddSubnet("web-subnet", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg);

        Assert.Same(nsg.Resource, subnet.Resource.NetworkSecurityGroup);
    }

    [Fact]
    public void WithSecurityRule_DuplicateName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsg = builder.AddNetworkSecurityGroup("web-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                SourceAddressPrefix = "*",
                SourcePortRange = "*",
                DestinationAddressPrefix = "*",
                DestinationPortRange = "443"
            });

        var exception = Assert.Throws<ArgumentException>(() => nsg.WithSecurityRule(new AzureSecurityRule
        {
            Name = "ALLOW-HTTPS",
            Priority = 110,
            Direction = SecurityRuleDirection.Inbound,
            Access = SecurityRuleAccess.Allow,
            Protocol = SecurityRuleProtocol.Tcp,
            SourceAddressPrefix = "*",
            SourcePortRange = "*",
            DestinationAddressPrefix = "*",
            DestinationPortRange = "443"
        }));

        Assert.Contains("allow-https", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleNSGs_WithSameRuleName_GeneratesDistinctBicepIdentifiers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");

        var nsg1 = builder.AddNetworkSecurityGroup("nsg-one")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                SourceAddressPrefix = "*",
                SourcePortRange = "*",
                DestinationAddressPrefix = "*",
                DestinationPortRange = "443"
            });

        var nsg2 = builder.AddNetworkSecurityGroup("nsg-two")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                SourceAddressPrefix = AzureServiceTags.VirtualNetwork,
                SourcePortRange = "*",
                DestinationAddressPrefix = "*",
                DestinationPortRange = "443"
            });

        vnet.AddSubnet("subnet1", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg1);
        vnet.AddSubnet("subnet2", "10.0.2.0/24")
            .WithNetworkSecurityGroup(nsg2);

        await AzureManifestUtils.VerifyAllAzureBicep(builder);
    }

    [Fact]
    public void WithNetworkSecurityGroup_AfterShorthand_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("web-nsg");
        var subnet = vnet.AddSubnet("web-subnet", "10.0.1.0/24")
            .AllowInbound(port: "443", from: AzureServiceTags.AzureLoadBalancer);

        var exception = Assert.Throws<InvalidOperationException>(() => subnet.WithNetworkSecurityGroup(nsg));

        Assert.Contains("shorthand", exception.Message);
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_ExistingWithSecurityRules_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingName = builder.AddParameter("existingNsgName");
        var nsg = builder.AddNetworkSecurityGroup("web-nsg")
            .PublishAsExisting(existingName, resourceGroupParameter: default)
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-https",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                DestinationPortRange = "443"
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsg.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_WithSourceAddressPrefixReference_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var pip = builder.AddPublicIPAddress("mypip");

        builder.AddNetworkSecurityGroup("web-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "allow-from-pip",
                Priority = 100,
                Direction = SecurityRuleDirection.Inbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                DestinationPortRange = "443",
                SourceAddressPrefixReference = ReferenceExpression.Create($"{pip.Resource.IpAddress}")
            });

        await AzureManifestUtils.VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task AddNetworkSecurityGroup_WithDestinationAddressPrefixReference_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var pip = builder.AddPublicIPAddress("mypip");

        builder.AddNetworkSecurityGroup("web-nsg")
            .WithSecurityRule(new AzureSecurityRule
            {
                Name = "route-to-pip",
                Priority = 100,
                Direction = SecurityRuleDirection.Outbound,
                Access = SecurityRuleAccess.Allow,
                Protocol = SecurityRuleProtocol.Tcp,
                DestinationPortRange = "443",
                DestinationAddressPrefixReference = ReferenceExpression.Create($"{pip.Resource.IpAddress}")
            });

        await AzureManifestUtils.VerifyAllAzureBicep(builder);
    }
}
