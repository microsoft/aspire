// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Virtual Network resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Virtual Network resource.</param>
public class AzureVirtualNetworkResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    private const string DefaultAddressPrefix = "10.0.0.0/16";

    // Backing field holds either string or ParameterResource
    private readonly object _addressPrefix = DefaultAddressPrefix;

    /// <summary>
    /// Gets the list of subnets for the virtual network.
    /// </summary>
    internal List<AzureSubnetResource> Subnets { get; } = [];

    /// <summary>
    /// Gets the address prefix for the virtual network (e.g., "10.0.0.0/16"), or <c>null</c> if the address prefix is provided via a <see cref="ParameterResource"/>.
    /// </summary>
    public string? AddressPrefix => _addressPrefix as string;

    /// <summary>
    /// Gets the parameter resource containing the address prefix for the virtual network, or <c>null</c> if the address prefix is provided as a literal string.
    /// </summary>
    public ParameterResource? AddressPrefixParameter => _addressPrefix as ParameterResource;

    /// <summary>
    /// Gets the "id" output reference from the Azure Virtual Network resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutput => new("name", this);

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureVirtualNetworkResource"/> class with a string address prefix.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure Virtual Network resource.</param>
    /// <param name="addressPrefix">The address prefix for the virtual network (e.g., "10.0.0.0/16").</param>
    public AzureVirtualNetworkResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, string? addressPrefix)
        : this(name, configureInfrastructure)
    {
        _addressPrefix = addressPrefix ?? DefaultAddressPrefix;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureVirtualNetworkResource"/> class with a parameterized address prefix.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure Virtual Network resource.</param>
    /// <param name="addressPrefix">The parameter resource containing the address prefix for the virtual network.</param>
    public AzureVirtualNetworkResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, ParameterResource addressPrefix)
        : this(name, configureInfrastructure)
    {
        ArgumentNullException.ThrowIfNull(addressPrefix);
        _addressPrefix = addressPrefix;
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a VirtualNetwork with the same identifier already exists
        var existingVNet = resources.OfType<VirtualNetwork>().SingleOrDefault(vnet => vnet.BicepIdentifier == bicepIdentifier);

        if (existingVNet is not null)
        {
            return existingVNet;
        }

        // Create and add new resource if it doesn't exist
        var vnet = VirtualNetwork.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            vnet))
        {
            vnet.Name = NameOutput.AsProvisioningParameter(infra);
        }

        infra.Add(vnet);
        return vnet;
    }
}
