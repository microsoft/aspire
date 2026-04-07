// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Private Endpoint resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="subnet">The subnet where the private endpoint will be created.</param>
/// <param name="target">The target Azure resource to connect via private link.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Private Endpoint resource.</param>
public class AzurePrivateEndpointResource(
    string name,
    AzureSubnetResource subnet,
    IAzurePrivateEndpointTarget target,
    Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the "id" output reference from the Azure Private Endpoint resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutput => new("name", this);

    /// <summary>
    /// Gets the subnet where the private endpoint will be created.
    /// </summary>
    public AzureSubnetResource Subnet { get; } = subnet;

    /// <summary>
    /// Gets the target Azure resource to connect via private link.
    /// </summary>
    public IAzurePrivateEndpointTarget Target { get; } = target;

    /// <summary>
    /// Gets or sets the Private DNS Zone for this endpoint.
    /// </summary>
    internal AzurePrivateDnsZoneResource? DnsZone { get; set; }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a PrivateEndpoint with the same identifier already exists
        var existingEndpoint = resources.OfType<PrivateEndpoint>().SingleOrDefault(endpoint => endpoint.BicepIdentifier == bicepIdentifier);

        if (existingEndpoint is not null)
        {
            return existingEndpoint;
        }

        // Create and add new resource if it doesn't exist
        var endpoint = PrivateEndpoint.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            endpoint))
        {
            endpoint.Name = NameOutput.AsProvisioningParameter(infra);
        }

        infra.Add(endpoint);
        return endpoint;
    }
}
