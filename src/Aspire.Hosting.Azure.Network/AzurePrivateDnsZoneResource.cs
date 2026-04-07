// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.PrivateDns;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Private DNS Zone resource.
/// </summary>
internal sealed class AzurePrivateDnsZoneResource : AzureProvisioningResource
{
    /// <summary>
    /// Initializes a new instance of <see cref="AzurePrivateDnsZoneResource"/>.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="zoneName">The DNS zone name (e.g., "privatelink.blob.core.windows.net").</param>
    public AzurePrivateDnsZoneResource(string name, string zoneName)
        : base(name, ConfigureDnsZone)
    {
        ZoneName = zoneName;
    }

    /// <summary>
    /// Gets the DNS zone name (e.g., "privatelink.blob.core.windows.net").
    /// </summary>
    public string ZoneName { get; }

    /// <summary>
    /// Gets the "id" output reference from the Private DNS Zone resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference from the Private DNS Zone resource.
    /// </summary>
    public BicepOutputReference NameOutput => new("name", this);

    /// <summary>
    /// Tracks VNet Links for this DNS Zone, keyed by VNet resource.
    /// </summary>
    internal Dictionary<AzureVirtualNetworkResource, AzurePrivateDnsZoneVNetLinkResource> VNetLinks { get; } = [];

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a PrivateDnsZone with the same identifier already exists
        var existingZone = resources.OfType<PrivateDnsZone>().SingleOrDefault(z => z.BicepIdentifier == bicepIdentifier);

        if (existingZone is not null)
        {
            return existingZone;
        }

        // Create and add new resource if it doesn't exist
        var dnsZone = PrivateDnsZone.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            dnsZone))
        {
            dnsZone.Name = NameOutput.AsProvisioningParameter(infra);
        }

        infra.Add(dnsZone);
        return dnsZone;
    }

    private static void ConfigureDnsZone(AzureResourceInfrastructure infra)
    {
        var resource = (AzurePrivateDnsZoneResource)infra.AspireResource;

        var dnsZone = new PrivateDnsZone(infra.AspireResource.GetBicepIdentifier())
        {
            Name = resource.ZoneName,
            Location = new AzureLocation("global"),
            Tags = { { "aspire-resource-name", resource.Name } }
        };
        infra.Add(dnsZone);

        // Create VNet Links for all linked VNets
        foreach (var vnetLinkEntry in resource.VNetLinks)
        {
            var vnetLink = vnetLinkEntry.Value;
            var linkIdentifier = Infrastructure.NormalizeBicepIdentifier($"{vnetLink.VNet.Name}_link");

            var link = new VirtualNetworkLink(linkIdentifier)
            {
                Name = $"{vnetLink.VNet.Name}-link",
                Parent = dnsZone,
                Location = new AzureLocation("global"),
                RegistrationEnabled = false,
                VirtualNetworkId = vnetLink.VNet.Id.AsProvisioningParameter(infra),
                Tags = { { "aspire-resource-name", vnetLink.Name } }
            };
            infra.Add(link);
        }

        // Output the DNS Zone ID for references
        infra.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = dnsZone.Id
        });

        infra.Add(new ProvisioningOutput("name", typeof(string))
        {
            Value = dnsZone.Name
        });
    }
}
