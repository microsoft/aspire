// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Network;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Network Security Group resources to the application model.
/// </summary>
public static class AzureNetworkSecurityGroupExtensions
{
    /// <summary>
    /// Adds an Azure Network Security Group to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the Network Security Group resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureNetworkSecurityGroupResource}"/>.</returns>
    /// <example>
    /// This example adds a Network Security Group with a security rule:
    /// <code>
    /// var nsg = builder.AddNetworkSecurityGroup("web-nsg")
    ///     .WithSecurityRule(new AzureSecurityRule
    ///     {
    ///         Name = "allow-https",
    ///         Priority = 100,
    ///         Direction = SecurityRuleDirection.Inbound,
    ///         Access = SecurityRuleAccess.Allow,
    ///         Protocol = SecurityRuleProtocol.Tcp,
    ///         DestinationPortRange = "443"
    ///     });
    /// </code>
    /// </example>
    [AspireExport("addNetworkSecurityGroup", Description = "Adds an Azure Network Security Group resource to the application model.")]
    public static IResourceBuilder<AzureNetworkSecurityGroupResource> AddNetworkSecurityGroup(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureNetworkSecurityGroupResource(name, ConfigureNetworkSecurityGroup);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Adds a security rule to the Network Security Group.
    /// </summary>
    /// <param name="builder">The Network Security Group resource builder.</param>
    /// <param name="rule">The security rule configuration.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureNetworkSecurityGroupResource}"/> for chaining.</returns>
    /// <example>
    /// This example adds multiple security rules to a Network Security Group:
    /// <code>
    /// var nsg = builder.AddNetworkSecurityGroup("web-nsg")
    ///     .WithSecurityRule(new AzureSecurityRule
    ///     {
    ///         Name = "allow-https",
    ///         Priority = 100,
    ///         Direction = SecurityRuleDirection.Inbound,
    ///         Access = SecurityRuleAccess.Allow,
    ///         Protocol = SecurityRuleProtocol.Tcp,
    ///         DestinationPortRange = "443"
    ///     })
    ///     .WithSecurityRule(new AzureSecurityRule
    ///     {
    ///         Name = "deny-all-inbound",
    ///         Priority = 4096,
    ///         Direction = SecurityRuleDirection.Inbound,
    ///         Access = SecurityRuleAccess.Deny,
    ///         Protocol = SecurityRuleProtocol.Asterisk,
    ///         DestinationPortRange = "*"
    ///     });
    /// </code>
    /// </example>
    [AspireExport("withSecurityRule", Description = "Adds a security rule to an Azure Network Security Group resource.")]
    public static IResourceBuilder<AzureNetworkSecurityGroupResource> WithSecurityRule(
        this IResourceBuilder<AzureNetworkSecurityGroupResource> builder,
        AzureSecurityRule rule)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrEmpty(rule.Name);

        if (builder.Resource.SecurityRules.Any(existing => string.Equals(existing.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"A security rule named '{rule.Name}' already exists in Network Security Group '{builder.Resource.Name}'.",
                nameof(rule));
        }

        builder.Resource.SecurityRules.Add(rule);
        return builder;
    }

    private static void ConfigureNetworkSecurityGroup(AzureResourceInfrastructure infra)
    {
        var azureResource = (AzureNetworkSecurityGroupResource)infra.AspireResource;

        var nsg = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infra,
            (identifier, name) =>
            {
                var resource = NetworkSecurityGroup.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                return new NetworkSecurityGroup(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                };
            });

        foreach (var rule in azureResource.SecurityRules)
        {
            var ruleIdentifier = Infrastructure.NormalizeBicepIdentifier($"{nsg.BicepIdentifier}_{rule.Name}");
            var securityRule = new SecurityRule(ruleIdentifier)
            {
                Name = rule.Name,
                Priority = rule.Priority,
                Direction = rule.Direction,
                Access = rule.Access,
                Protocol = rule.Protocol,
                SourcePortRange = rule.SourcePortRange,
                DestinationPortRange = rule.DestinationPortRange,
                Parent = nsg,
            };

            if (rule.Description is { } description)
            {
                securityRule.Description = description;
            }

            // When a reference expression is provided (e.g. a PIP IP resolved at deploy time),
            // use it as a provisioning parameter; otherwise use the static address prefix.
            if (rule.SourceAddressPrefixReference is { } sourceAddressReference)
            {
                securityRule.SourceAddressPrefix = sourceAddressReference.AsProvisioningParameter(infra);
            }
            else
            {
                securityRule.SourceAddressPrefix = rule.SourceAddressPrefix;
            }

            if (rule.DestinationAddressPrefixReference is { } destinationAddressReference)
            {
                securityRule.DestinationAddressPrefix = destinationAddressReference.AsProvisioningParameter(infra);
            }
            else
            {
                securityRule.DestinationAddressPrefix = rule.DestinationAddressPrefix;
            }

            infra.Add(securityRule);
        }

        infra.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = nsg.Id
        });

        infra.Add(new ProvisioningOutput("name", typeof(string))
        {
            Value = nsg.Name
        });
    }
}
