// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Network;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Network Security Perimeter resources to the application model.
/// </summary>
public static class AzureNetworkSecurityPerimeterExtensions
{
    /// <summary>
    /// Adds an Azure Network Security Perimeter to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the Network Security Perimeter resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureNetworkSecurityPerimeterResource}"/>.</returns>
    /// <example>
    /// This example adds a Network Security Perimeter and associates a storage resource:
    /// <code>
    /// var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");
    /// var storage = builder.AddAzureStorage("storage");
    /// storage.AssociateWith(nsp);
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an Azure Network Security Perimeter resource to the application model.")]
    public static IResourceBuilder<AzureNetworkSecurityPerimeterResource> AddNetworkSecurityPerimeter(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureNetworkSecurityPerimeterResource(name, ConfigureNetworkSecurityPerimeter);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Adds an access rule to the Network Security Perimeter.
    /// </summary>
    /// <param name="builder">The Network Security Perimeter resource builder.</param>
    /// <param name="rule">The access rule configuration.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureNetworkSecurityPerimeterResource}"/> for chaining.</returns>
    /// <example>
    /// This example adds inbound and outbound access rules:
    /// <code>
    /// var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
    ///     .WithAccessRule(new AzureNspAccessRule
    ///     {
    ///         Name = "allow-my-ip",
    ///         Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
    ///         AddressPrefixes = { "203.0.113.0/24" }
    ///     })
    ///     .WithAccessRule(new AzureNspAccessRule
    ///     {
    ///         Name = "allow-outbound-fqdn",
    ///         Direction = NetworkSecurityPerimeterAccessRuleDirection.Outbound,
    ///         FullyQualifiedDomainNames = { "*.blob.core.windows.net" }
    ///     });
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an access rule to an Azure Network Security Perimeter resource.")]
    public static IResourceBuilder<AzureNetworkSecurityPerimeterResource> WithAccessRule(
        this IResourceBuilder<AzureNetworkSecurityPerimeterResource> builder,
        AzureNspAccessRule rule)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrEmpty(rule.Name);

        if (builder.Resource.AccessRules.Any(existing => string.Equals(existing.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"An access rule named '{rule.Name}' already exists in Network Security Perimeter '{builder.Resource.Name}'.",
                nameof(rule));
        }

        builder.Resource.AccessRules.Add(rule);
        return builder;
    }

    /// <summary>
    /// Associates an Azure PaaS resource with a Network Security Perimeter.
    /// </summary>
    /// <param name="target">The target PaaS resource builder to associate.</param>
    /// <param name="nsp">The Network Security Perimeter to associate with.</param>
    /// <returns>A reference to the target resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The association uses <see cref="NetworkSecurityPerimeterAssociationAccessMode.Enforced"/> mode,
    /// which means resources within the perimeter can communicate with each other, but public access
    /// is restricted to the rules defined in the perimeter profile.
    /// </para>
    /// <para>
    /// When a resource is associated with an NSP, the resource's <c>publicNetworkAccess</c> is automatically
    /// set to <c>"SecuredByPerimeter"</c>. To override this, use
    /// <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// This example associates storage and key vault resources with an NSP:
    /// <code>
    /// var nsp = builder.AddNetworkSecurityPerimeter("my-nsp");
    /// var storage = builder.AddAzureStorage("storage");
    /// var keyVault = builder.AddAzureKeyVault("kv");
    ///
    /// storage.AssociateWith(nsp);
    /// keyVault.AssociateWith(nsp);
    /// </code>
    /// </example>
    [AspireExport("associateWithNsp", Description = "Associates an Azure PaaS resource with a Network Security Perimeter.")]
    public static IResourceBuilder<T> AssociateWith<T>(
        this IResourceBuilder<T> target,
        IResourceBuilder<AzureNetworkSecurityPerimeterResource> nsp) where T : IResource, IAzureNspAssociationTarget
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(nsp);

        var associationName = $"{target.Resource.Name}-assoc";

        nsp.Resource.Associations.Add(new AzureNetworkSecurityPerimeterResource.NspAssociationConfig(
            associationName,
            target.Resource.Id));

        return target;
    }

    private static void ConfigureNetworkSecurityPerimeter(AzureResourceInfrastructure infra)
    {
        var azureResource = (AzureNetworkSecurityPerimeterResource)infra.AspireResource;

        var nsp = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infra,
            (identifier, name) =>
            {
                var resource = NetworkSecurityPerimeter.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                return new NetworkSecurityPerimeter(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                };
            });

        // Create a default profile
        var profileIdentifier = Infrastructure.NormalizeBicepIdentifier($"{nsp.BicepIdentifier}_profile");
        var profile = new NetworkSecurityPerimeterProfile(profileIdentifier)
        {
            Name = "defaultProfile",
            Parent = nsp,
        };
        infra.Add(profile);

        // Add access rules to the profile
        foreach (var rule in azureResource.AccessRules)
        {
            var ruleIdentifier = Infrastructure.NormalizeBicepIdentifier($"{profileIdentifier}_{rule.Name}");
            var accessRule = new NetworkSecurityPerimeterAccessRule(ruleIdentifier)
            {
                Name = rule.Name,
                Direction = rule.Direction,
                Parent = profile,
            };

            foreach (var prefix in rule.AddressPrefixes)
            {
                accessRule.AddressPrefixes.Add(prefix);
            }

            foreach (var prefixReference in rule.AddressPrefixReferences)
            {
                accessRule.AddressPrefixes.Add(prefixReference.AsProvisioningParameter(infra));
            }

            foreach (var sub in rule.Subscriptions)
            {
                accessRule.Subscriptions.Add(new WritableSubResource { Id = new ResourceIdentifier(sub) });
            }

            foreach (var subReference in rule.SubscriptionReferences)
            {
                accessRule.Subscriptions.Add(new WritableSubResource { Id = subReference.AsProvisioningParameter(infra) });
            }

            foreach (var fqdn in rule.FullyQualifiedDomainNames)
            {
                accessRule.FullyQualifiedDomainNames.Add(fqdn);
            }

            foreach (var fqdnReference in rule.FullyQualifiedDomainNameReferences)
            {
                accessRule.FullyQualifiedDomainNames.Add(fqdnReference.AsProvisioningParameter(infra));
            }

            infra.Add(accessRule);
        }

        // Add resource associations
        foreach (var association in azureResource.Associations)
        {
            var assocIdentifier = Infrastructure.NormalizeBicepIdentifier($"{nsp.BicepIdentifier}_{association.Name}");
            var nspAssociation = new NetworkSecurityPerimeterAssociation(assocIdentifier)
            {
                Name = association.Name,
                Parent = nsp,
                AccessMode = NetworkSecurityPerimeterAssociationAccessMode.Enforced,
                PrivateLinkResourceId = association.TargetResourceId.AsProvisioningParameter(infra),
                ProfileId = profile.Id,
            };

            infra.Add(nspAssociation);
        }

        infra.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = nsp.Id
        });

        infra.Add(new ProvisioningOutput("name", typeof(string))
        {
            Value = nsp.Name
        });
    }
}
