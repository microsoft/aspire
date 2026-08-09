// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Network;

namespace Aspire.Hosting.Azure.Tests;

public class AzureNetworkSecurityPerimeterExtensionsTests
{
    [Fact]
    public void AddNetworkSecurityPerimeter_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");

        Assert.NotNull(nsp);
        Assert.Equal("my-nsp", nsp.Resource.Name);
        Assert.IsType<AzureNetworkSecurityPerimeterResource>(nsp.Resource);
    }

    [Fact]
    public void AddNetworkSecurityPerimeter_InRunMode_DoesNotAddToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");

        Assert.DoesNotContain(nsp.Resource, builder.Resources);
    }

    [Fact]
    public async Task AddNetworkSecurityPerimeter_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsp.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityPerimeter_WithAccessRules_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-my-ip",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
                AddressPrefixes = { "203.0.113.0/24" }
            })
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-outbound-fqdn",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Outbound,
                FullyQualifiedDomainNames = { "*.blob.core.windows.net" }
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsp.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityPerimeter_WithSubscriptionRule_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-subscription",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
                Subscriptions = { "/subscriptions/00000000-0000-0000-0000-000000000001" }
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsp.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityPerimeter_WithParameterBasedAccessRules_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var inboundAddressPrefix = builder.AddParameter("inboundAddressPrefix");
        var allowedSubscription = builder.AddParameter("allowedSubscription");
        var outboundFqdn = builder.AddParameter("outboundFqdn");

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-my-ip",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
                AddressPrefixes = { "203.0.113.0/24" },
                AddressPrefixReferences = { ReferenceExpression.Create($"{inboundAddressPrefix}") }
            })
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-subscription",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
                Subscriptions = { "/subscriptions/00000000-0000-0000-0000-000000000001" },
                SubscriptionReferences = { ReferenceExpression.Create($"{allowedSubscription}") }
            })
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-outbound-fqdn",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Outbound,
                FullyQualifiedDomainNames = { "*.blob.core.windows.net" },
                FullyQualifiedDomainNameReferences = { ReferenceExpression.Create($"{outboundFqdn}") }
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsp.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityPerimeter_WithStorageAssociation_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");
        var storage = builder.AddAzureStorage("storage");

        storage.WithNetworkSecurityPerimeter(nsp);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsp.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddNetworkSecurityPerimeter_WithMultipleAssociations_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-my-ip",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
                AddressPrefixes = { "203.0.113.0/24" }
            });

        var storage = builder.AddAzureStorage("storage");
        var keyVault = builder.AddAzureKeyVault("kv");

        storage.WithNetworkSecurityPerimeter(nsp);
        keyVault.WithNetworkSecurityPerimeter(nsp, NetworkSecurityPerimeterAssociationAccessMode.Learning);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(nsp.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void WithAccessRule_DuplicateName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
            .WithAccessRule(new AzureNspAccessRule
            {
                Name = "allow-my-ip",
                Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
                AddressPrefixes = { "203.0.113.0/24" }
            });

        var exception = Assert.Throws<ArgumentException>(() => nsp.WithAccessRule(new AzureNspAccessRule
        {
            Name = "ALLOW-MY-IP",
            Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
            AddressPrefixes = { "10.0.0.0/8" }
        }));

        Assert.Contains("allow-my-ip", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssociateWith_DuplicateAssociationName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");
        var storage = builder.AddAzureStorage("storage");

        storage.WithNetworkSecurityPerimeter(nsp);

        var exception = Assert.Throws<ArgumentException>(() => storage.WithNetworkSecurityPerimeter(nsp));

        Assert.Contains("storage-assoc", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

}
