// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure NAT Gateway resource.
/// </summary>
/// <remarks>
/// A NAT Gateway provides outbound internet connectivity for resources in a virtual network subnet.
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/>
/// to configure specific <see cref="Azure.Provisioning"/> properties.
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure NAT Gateway resource.</param>
public class AzureNatGatewayResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the "id" output reference from the Azure NAT Gateway resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutput => new("name", this);

    /// <summary>
    /// Gets the list of explicit Public IP Address resources associated with this NAT Gateway.
    /// </summary>
    internal List<AzurePublicIPAddressResource> PublicIPAddresses { get; } = [];

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        var existing = resources.OfType<NatGateway>().SingleOrDefault(r => r.BicepIdentifier == bicepIdentifier);

        if (existing is not null)
        {
            return existing;
        }

        var natGw = NatGateway.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, natGw))
        {
            natGw.Name = NameOutput.AsProvisioningParameter(infra);
        }

        infra.Add(natGw);
        return natGw;
    }
}
