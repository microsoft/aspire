// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Utils;
using Azure.Provisioning.Network;

namespace Aspire.Hosting.Azure.Tests;

public class AzureVirtualNetworkExtensionsTests
{
    [Fact]
    public void AddAzureVirtualNetwork_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");

        Assert.NotNull(vnet);
        Assert.Equal("myvnet", vnet.Resource.Name);
        Assert.IsType<AzureVirtualNetworkResource>(vnet.Resource);
    }

    [Fact]
    public void AddAzureVirtualNetwork_WithCustomAddressPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet", "10.1.0.0/16");

        Assert.NotNull(vnet);
        Assert.Equal("myvnet", vnet.Resource.Name);
        Assert.Equal("10.1.0.0/16", vnet.Resource.AddressPrefix);
        Assert.Null(vnet.Resource.AddressPrefixParameter);
    }

    [Fact]
    public void AddAzureVirtualNetwork_WithParameterResource_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnetPrefixParam = builder.AddParameter("vnetPrefix");
        var vnet = builder.AddAzureVirtualNetwork("myvnet", vnetPrefixParam);

        Assert.NotNull(vnet);
        Assert.Equal("myvnet", vnet.Resource.Name);
        Assert.Null(vnet.Resource.AddressPrefix);
        Assert.Same(vnetPrefixParam.Resource, vnet.Resource.AddressPrefixParameter);
    }

    [Fact]
    public async Task AddAzureVirtualNetwork_WithParameterResource_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnetPrefixParam = builder.AddParameter("vnetPrefix");
        var vnet = builder.AddAzureVirtualNetwork("myvnet", vnetPrefixParam);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);

        await Verify(manifest.BicepText, extension: "bicep")
            .UseMethodName("AddAzureVirtualNetwork_WithParameterResource_GeneratesBicep");
    }

    [Fact]
    public void AddSubnet_CreatesSubnetResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", "10.0.1.0/24");

        Assert.NotNull(subnet);
        Assert.Equal("mysubnet", subnet.Resource.Name);
        Assert.Equal("mysubnet", subnet.Resource.SubnetName);
        Assert.Equal("10.0.1.0/24", subnet.Resource.AddressPrefix);
        Assert.Same(vnet.Resource, subnet.Resource.Parent);
    }

    [Fact]
    public void AddSubnet_WithCustomSubnetName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", "10.0.1.0/24", subnetName: "custom-subnet-name");

        Assert.Equal("mysubnet", subnet.Resource.Name);
        Assert.Equal("custom-subnet-name", subnet.Resource.SubnetName);
        Assert.Equal("10.0.1.0/24", subnet.Resource.AddressPrefix);
    }

    [Fact]
    public void AddSubnet_MultipleSubnets_HaveDifferentParentReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet1 = vnet.AddSubnet("subnet1", "10.0.1.0/24");
        var subnet2 = vnet.AddSubnet("subnet2", "10.0.2.0/24");

        // Both subnets should have the same parent VNet
        Assert.Same(vnet.Resource, subnet1.Resource.Parent);
        Assert.Same(vnet.Resource, subnet2.Resource.Parent);
        // But they should be different resources
        Assert.NotSame(subnet1.Resource, subnet2.Resource);
    }

    [Fact]
    public async Task AddAzureVirtualNetwork_WithSubnets_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        vnet.AddSubnet("subnet1", "10.0.1.0/24")
            .WithAnnotation(new AzureSubnetServiceDelegationAnnotation("ContainerAppsDelegation", "Microsoft.App/environments"));
        vnet.AddSubnet("subnet2", "10.0.2.0/24", subnetName: "custom-subnet-name");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddAzureVirtualNetwork_InRunMode_DoesNotAddToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", "10.0.1.0/24");

        // In run mode, the resource should not be added to the builder's resources
        Assert.DoesNotContain(vnet.Resource, builder.Resources);
        // In run mode, the subnet should not be added to the builder's resources
        Assert.DoesNotContain(subnet.Resource, builder.Resources);
    }

    [Fact]
    public void WithDelegatedSubnet_AddsAnnotationsToSubnetAndTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", "10.0.0.0/23");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(subnet);

        // Verify the target has DelegatedSubnetAnnotation
        var subnetAnnotation = env.Resource.Annotations.OfType<DelegatedSubnetAnnotation>().SingleOrDefault();
        Assert.NotNull(subnetAnnotation);
        Assert.Equal("{myvnet.outputs.mysubnet_Id}", subnetAnnotation.SubnetId.ValueExpression);

        // Verify the subnet has AzureSubnetServiceDelegationAnnotation
        var delegationAnnotation = subnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>().SingleOrDefault();
        Assert.NotNull(delegationAnnotation);
        Assert.Equal("Microsoft.App/environments", delegationAnnotation.ServiceName);
    }

    [Fact]
    public void AddSubnet_WithParameterResource_CreatesSubnetResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var addressPrefixParam = builder.AddParameter("subnetPrefix");
        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", addressPrefixParam);

        Assert.NotNull(subnet);
        Assert.Equal("mysubnet", subnet.Resource.Name);
        Assert.Equal("mysubnet", subnet.Resource.SubnetName);
        Assert.Null(subnet.Resource.AddressPrefix);
        Assert.Same(addressPrefixParam.Resource, subnet.Resource.AddressPrefixParameter);
        Assert.Same(vnet.Resource, subnet.Resource.Parent);
    }

    [Fact]
    public void AddSubnet_WithParameterResource_AndCustomSubnetName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var addressPrefixParam = builder.AddParameter("subnetPrefix");
        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("mysubnet", addressPrefixParam, subnetName: "custom-subnet-name");

        Assert.Equal("mysubnet", subnet.Resource.Name);
        Assert.Equal("custom-subnet-name", subnet.Resource.SubnetName);
        Assert.Null(subnet.Resource.AddressPrefix);
        Assert.Same(addressPrefixParam.Resource, subnet.Resource.AddressPrefixParameter);
    }

    [Fact]
    public async Task AddSubnet_WithParameterResource_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var addressPrefixParam = builder.AddParameter("subnetPrefix");
        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        vnet.AddSubnet("mysubnet", addressPrefixParam);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddSubnet_WithNatGateway_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var natGw = builder.AddNatGateway("mynat");
        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        vnet.AddSubnet("mysubnet", "10.0.1.0/24")
            .WithNatGateway(natGw);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AllowInbound_AutoCreatesNsg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", from: "AzureLoadBalancer", protocol: SecurityRuleProtocol.Tcp);

        Assert.NotNull(subnet.Resource.NetworkSecurityGroup);
        Assert.Equal("web-nsg", subnet.Resource.NetworkSecurityGroup.Name);
        Assert.Single(subnet.Resource.NetworkSecurityGroup.SecurityRules);
    }

    [Fact]
    public void AllowInbound_UsesExistingNsg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var nsg = builder.AddNetworkSecurityGroup("my-nsg");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .WithNetworkSecurityGroup(nsg)
            .AllowInbound(port: "443", from: "AzureLoadBalancer");

        Assert.Same(nsg.Resource, subnet.Resource.NetworkSecurityGroup);
        Assert.Single(nsg.Resource.SecurityRules);
    }

    [Fact]
    public void Shorthand_AutoIncrementsPriority()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", from: "AzureLoadBalancer")
            .DenyInbound(from: "VirtualNetwork")
            .DenyInbound(from: "Internet");

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(3, rules.Count);
        Assert.Equal(100, rules[0].Priority);
        Assert.Equal(200, rules[1].Priority);
        Assert.Equal(300, rules[2].Priority);
    }

    [Fact]
    public void Shorthand_ExplicitPriorityOverridesAutoIncrement()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", priority: 500)
            .DenyInbound(from: "Internet");

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(500, rules[0].Priority);
        Assert.Equal(600, rules[1].Priority); // auto-increments from max (500) + 100
    }

    [Fact]
    public void Shorthand_AutoGeneratesRuleNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", from: "AzureLoadBalancer")
            .DenyInbound(from: "Internet")
            .AllowOutbound(port: "443")
            .DenyOutbound();

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal("allow-inbound-443-AzureLoadBalancer", rules[0].Name);
        Assert.Equal("deny-inbound-Internet", rules[1].Name);
        Assert.Equal("allow-outbound-443", rules[2].Name);
        Assert.Equal("deny-outbound", rules[3].Name);
    }

    [Fact]
    public void Shorthand_ExplicitNameOverridesAutoGeneration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", name: "my-custom-rule");

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal("my-custom-rule", rules[0].Name);
    }

    [Fact]
    public void Shorthand_IncludesToInGeneratedName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("aci-subnet", "10.0.3.0/28")
            .AllowOutbound(port: "443", to: AzureServiceTags.AzureActiveDirectory, protocol: SecurityRuleProtocol.Tcp)
            .AllowOutbound(port: "443", to: AzureServiceTags.Sql, protocol: SecurityRuleProtocol.Tcp);

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(2, rules.Count);
        Assert.Equal("allow-outbound-443-AzureActiveDirectory", rules[0].Name);
        Assert.Equal("allow-outbound-443-Sql", rules[1].Name);
    }

    [Fact]
    public void Shorthand_IncludesFromAndToInGeneratedName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", from: AzureServiceTags.AzureLoadBalancer, to: AzureServiceTags.VirtualNetwork);

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal("allow-inbound-443-AzureLoadBalancer-VirtualNetwork", rules[0].Name);
    }

    [Fact]
    public void Shorthand_DeduplicatesConflictingNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .DenyInbound()
            .DenyInbound()
            .DenyInbound();

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(3, rules.Count);
        Assert.Equal("deny-inbound", rules[0].Name);
        Assert.Equal("deny-inbound-2", rules[1].Name);
        Assert.Equal("deny-inbound-3", rules[2].Name);
    }

    [Fact]
    public void Shorthand_DefaultsProtocolToAsterisk()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .DenyInbound(from: "Internet");

        var rule = Assert.Single(subnet.Resource.NetworkSecurityGroup!.SecurityRules);
        Assert.Equal(SecurityRuleProtocol.Asterisk, rule.Protocol);
    }

    [Fact]
    public void Shorthand_DefaultsPortsAndAddressesToWildcard()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .DenyInbound();

        var rule = Assert.Single(subnet.Resource.NetworkSecurityGroup!.SecurityRules);
        Assert.Equal("*", rule.SourcePortRange);
        Assert.Equal("*", rule.SourceAddressPrefix);
        Assert.Equal("*", rule.DestinationAddressPrefix);
        Assert.Equal("*", rule.DestinationPortRange);
    }

    [Fact]
    public async Task Shorthand_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", from: "AzureLoadBalancer", protocol: SecurityRuleProtocol.Tcp)
            .DenyInbound(from: "VirtualNetwork")
            .DenyInbound(from: "Internet");

        var vnetManifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);
        var nsgManifest = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource.Subnets[0].NetworkSecurityGroup!);

        await Verify(vnetManifest.BicepText, extension: "bicep")
            .AppendContentAsFile(nsgManifest.BicepText, "bicep", "nsg");
    }

    [Fact]
    public void ServiceTags_CanBeUsedAsFromAndToParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443", from: AzureServiceTags.AzureLoadBalancer, protocol: SecurityRuleProtocol.Tcp)
            .DenyInbound(from: AzureServiceTags.Internet)
            .AllowOutbound(port: "443", to: AzureServiceTags.Storage)
            .DenyOutbound(to: AzureServiceTags.VirtualNetwork);

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(4, rules.Count);

        Assert.Equal("AzureLoadBalancer", rules[0].SourceAddressPrefix);
        Assert.Equal("Internet", rules[1].SourceAddressPrefix);
        Assert.Equal("Storage", rules[2].DestinationAddressPrefix);
        Assert.Equal("VirtualNetwork", rules[3].DestinationAddressPrefix);
    }

    [Fact]
    public void ServiceTags_CanBeUsedInSecurityRuleProperties()
    {
        var rule = new AzureSecurityRule
        {
            Name = "allow-https-from-lb",
            Priority = 100,
            Direction = SecurityRuleDirection.Inbound,
            Access = SecurityRuleAccess.Allow,
            Protocol = SecurityRuleProtocol.Tcp,
            SourceAddressPrefix = AzureServiceTags.AzureLoadBalancer,
            DestinationAddressPrefix = AzureServiceTags.VirtualNetwork,
            DestinationPortRange = "443"
        };

        Assert.Equal("AzureLoadBalancer", rule.SourceAddressPrefix);
        Assert.Equal("VirtualNetwork", rule.DestinationAddressPrefix);
    }

    [Fact]
    public void ServiceTags_HaveExpectedValues()
    {
        Assert.Equal("Internet", AzureServiceTags.Internet);
        Assert.Equal("VirtualNetwork", AzureServiceTags.VirtualNetwork);
        Assert.Equal("AzureLoadBalancer", AzureServiceTags.AzureLoadBalancer);
        Assert.Equal("AzureTrafficManager", AzureServiceTags.AzureTrafficManager);
        Assert.Equal("Storage", AzureServiceTags.Storage);
        Assert.Equal("Sql", AzureServiceTags.Sql);
        Assert.Equal("AzureCosmosDB", AzureServiceTags.AzureCosmosDB);
        Assert.Equal("AzureKeyVault", AzureServiceTags.AzureKeyVault);
        Assert.Equal("EventHub", AzureServiceTags.EventHub);
        Assert.Equal("ServiceBus", AzureServiceTags.ServiceBus);
        Assert.Equal("AzureContainerRegistry", AzureServiceTags.AzureContainerRegistry);
        Assert.Equal("AppService", AzureServiceTags.AppService);
        Assert.Equal("AzureActiveDirectory", AzureServiceTags.AzureActiveDirectory);
        Assert.Equal("AzureMonitor", AzureServiceTags.AzureMonitor);
        Assert.Equal("GatewayManager", AzureServiceTags.GatewayManager);
    }

    [Fact]
    public void AllFourDirectionAccessCombos_SetCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
            .AllowInbound(port: "443")
            .DenyInbound(from: "Internet")
            .AllowOutbound(port: "443")
            .DenyOutbound(to: "Internet");

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(4, rules.Count);

        Assert.Equal(SecurityRuleAccess.Allow, rules[0].Access);
        Assert.Equal(SecurityRuleDirection.Inbound, rules[0].Direction);

        Assert.Equal(SecurityRuleAccess.Deny, rules[1].Access);
        Assert.Equal(SecurityRuleDirection.Inbound, rules[1].Direction);

        Assert.Equal(SecurityRuleAccess.Allow, rules[2].Access);
        Assert.Equal(SecurityRuleDirection.Outbound, rules[2].Direction);

        Assert.Equal(SecurityRuleAccess.Deny, rules[3].Access);
        Assert.Equal(SecurityRuleDirection.Outbound, rules[3].Direction);
    }
}
