// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Network;
using Azure.Provisioning.PrivateDns;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Private Endpoint resources to the application model.
/// </summary>
public static class AzurePrivateEndpointExtensions
{
    /// <summary>
    /// Adds an Azure Private Endpoint resource to the subnet.
    /// </summary>
    /// <param name="subnet">The subnet to add the private endpoint to.</param>
    /// <param name="target">The target Azure resource to connect via private link.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzurePrivateEndpointResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method automatically creates the Private DNS Zone, VNet Link, and DNS Zone Group
    /// required for private endpoint DNS resolution. Private DNS Zones are shared across
    /// multiple private endpoints that use the same zone name.
    /// </para>
    /// <para>
    /// When a private endpoint is added, the target resource (or its parent) is automatically
    /// configured to deny public network access. To override this behavior, use
    /// <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}"/> to customize
    /// the network settings.
    /// </para>
    /// </remarks>
    /// <example>
    /// This example creates a virtual network with a subnet and adds a private endpoint for Azure Storage blobs:
    /// <code>
    /// var vnet = builder.AddAzureVirtualNetwork("vnet");
    /// var peSubnet = vnet.AddSubnet("pe-subnet", "10.0.1.0/24");
    ///
    /// var storage = builder.AddAzureStorage("storage");
    /// var blobs = storage.AddBlobs("blobs");
    ///
    /// peSubnet.AddPrivateEndpoint(blobs);
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an Azure Private Endpoint resource to an Azure subnet resource.")]
    public static IResourceBuilder<AzurePrivateEndpointResource> AddPrivateEndpoint(
        this IResourceBuilder<AzureSubnetResource> subnet,
        IResourceBuilder<IAzurePrivateEndpointTarget> target)
    {
        ArgumentNullException.ThrowIfNull(subnet);
        ArgumentNullException.ThrowIfNull(target);

        var builder = subnet.ApplicationBuilder;
        var name = $"{subnet.Resource.Name}-{target.Resource.Name}-pe";
        var vnet = subnet.Resource.Parent;

        var resource = new AzurePrivateEndpointResource(name, subnet.Resource, target.Resource, ConfigurePrivateEndpoint);

        if (builder.ExecutionContext.IsRunMode)
        {
            // In run mode, we don't want to add the resource to the builder.
            return builder.CreateResourceBuilder(resource);
        }

        // Get or create the shared Private DNS Zone for this zone name
        var zoneName = target.Resource.GetPrivateDnsZoneName();
        var dnsZone = GetOrCreatePrivateDnsZone(builder, zoneName, vnet);
        resource.DnsZone = dnsZone;

        // Add annotation to the target's root parent (e.g., storage account) to signal
        // that it should deny public network access and to associate the private endpoint
        // for prerequisite provisioning dependency discovery.
        // This should only be done in publish mode. In run mode, the target resource
        // needs to be accessible over the public internet so the local app can reach it.
        IResource rootResource = target.Resource;
        while (rootResource is IResourceWithParent parentedResource)
        {
            rootResource = parentedResource.Parent;
        }
        rootResource.Annotations.Add(new PrivateEndpointTargetAnnotation(resource));

        var pe = builder.AddResource(resource);

        if (target.Resource is IAzurePrivateEndpointTargetNotification notificationTarget)
        {
            notificationTarget.OnPrivateEndpointCreated(pe);
        }

        return pe;

        void ConfigurePrivateEndpoint(AzureResourceInfrastructure infra)
        {
            var azureResource = (AzurePrivateEndpointResource)infra.AspireResource;

            // Get the shared DNS Zone as an existing resource
            var dnsZone = azureResource.DnsZone!;
            var dnsZoneIdentifier = dnsZone.GetBicepIdentifier();
            var privateDnsZone = PrivateDnsZone.FromExisting(dnsZoneIdentifier);
            privateDnsZone.Name = dnsZone.NameOutput.AsProvisioningParameter(infra);
            infra.Add(privateDnsZone);

            // Create the Private Endpoint
            var endpoint = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infra,
                (identifier, name) =>
                {
                    var resource = PrivateEndpoint.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) =>
                {
                    var pe = new PrivateEndpoint(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };

                    // Configure subnet
                    pe.Subnet.Id = azureResource.Subnet.Id.AsProvisioningParameter(infrastructure);

                    // Configure private link service connection
                    pe.PrivateLinkServiceConnections.Add(
                        new NetworkPrivateLinkServiceConnection
                        {
                            Name = $"{azureResource.Name}-connection",
                            PrivateLinkServiceId = azureResource.Target.Id.AsProvisioningParameter(infrastructure),
                            GroupIds = [.. azureResource.Target.GetPrivateLinkGroupIds()]
                        });

                    return pe;
                });

            // Create DNS Zone Group on the Private Endpoint
            var dnsZoneGroupIdentifier = $"{endpoint.BicepIdentifier}_dnsgroup";
            var dnsZoneGroup = new PrivateDnsZoneGroup(dnsZoneGroupIdentifier)
            {
                Name = "default",
                Parent = endpoint,
                PrivateDnsZoneConfigs =
                {
                    new PrivateDnsZoneConfig
                    {
                        Name = dnsZoneIdentifier,
                        PrivateDnsZoneId = privateDnsZone.Id
                    }
                }
            };
            infra.Add(dnsZoneGroup);

            // Output the Private Endpoint ID for references
            infra.Add(new ProvisioningOutput("id", typeof(string))
            {
                Value = endpoint.Id
            });

            // We need to output name so it can be referenced by others.
            infra.Add(new ProvisioningOutput("name", typeof(string)) { Value = endpoint.Name });
        }
    }

    /// <summary>
    /// Gets or creates a shared Private DNS Zone for the given zone name and VNet.
    /// </summary>
    private static AzurePrivateDnsZoneResource GetOrCreatePrivateDnsZone(
        IDistributedApplicationBuilder builder,
        string zoneName,
        AzureVirtualNetworkResource vnet)
    {
        // Search for existing DNS Zone with matching zone name
        var existingZone = builder.Resources
            .OfType<AzurePrivateDnsZoneResource>()
            .FirstOrDefault(z => z.ZoneName == zoneName);

        AzurePrivateDnsZoneResource dnsZone;

        if (existingZone is not null)
        {
            dnsZone = existingZone;
        }
        else
        {
            // Create new DNS Zone resource - use hyphens for resource name
            var zoneResourceName = zoneName.Replace(".", "-");
            dnsZone = new AzurePrivateDnsZoneResource(zoneResourceName, zoneName);
            builder.AddResource(dnsZone);
        }

        // Check if VNet Link already exists for this VNet
        if (!dnsZone.VNetLinks.ContainsKey(vnet))
        {
            // Create VNet Link resource
            var linkName = $"{dnsZone.Name}-{vnet.Name}-link";
            var vnetLink = new AzurePrivateDnsZoneVNetLinkResource(linkName, dnsZone, vnet);
            dnsZone.VNetLinks[vnet] = vnetLink;

            builder.AddResource(vnetLink).ExcludeFromManifest();
        }

        return dnsZone;
    }
}
