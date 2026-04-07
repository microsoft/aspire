// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Subnet resource.
/// </summary>
/// <remarks>
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/> to configure specific <see cref="Azure.Provisioning"/> properties.
/// </remarks>
public class AzureSubnetResource : Resource, IResourceWithParent<AzureVirtualNetworkResource>
{
    // Backing field holds either string or ParameterResource
    private readonly object _addressPrefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSubnetResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="subnetName">The subnet name.</param>
    /// <param name="addressPrefix">The address prefix for the subnet.</param>
    /// <param name="parent">The parent Virtual Network resource.</param>
    public AzureSubnetResource(string name, string subnetName, string addressPrefix, AzureVirtualNetworkResource parent)
        : base(name)
    {
        SubnetName = ThrowIfNullOrEmpty(subnetName);
        _addressPrefix = ThrowIfNullOrEmpty(addressPrefix);
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSubnetResource"/> class with a parameterized address prefix.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="subnetName">The subnet name.</param>
    /// <param name="addressPrefix">The parameter resource containing the address prefix for the subnet.</param>
    /// <param name="parent">The parent Virtual Network resource.</param>
    public AzureSubnetResource(string name, string subnetName, ParameterResource addressPrefix, AzureVirtualNetworkResource parent)
        : base(name)
    {
        SubnetName = ThrowIfNullOrEmpty(subnetName);
        _addressPrefix = addressPrefix ?? throw new ArgumentNullException(nameof(addressPrefix));
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Gets the subnet name.
    /// </summary>
    public string SubnetName { get; }

    /// <summary>
    /// Gets the address prefix for the subnet (e.g., "10.0.1.0/24"), or <c>null</c> if the address prefix is provided via a <see cref="ParameterResource"/>.
    /// </summary>
    public string? AddressPrefix => _addressPrefix as string;

    /// <summary>
    /// Gets the parameter resource containing the address prefix for the subnet, or <c>null</c> if the address prefix is provided as a literal string.
    /// </summary>
    public ParameterResource? AddressPrefixParameter => _addressPrefix as ParameterResource;

    /// <summary>
    /// Gets the subnet Id output reference.
    /// </summary>
    public BicepOutputReference Id => new($"{Infrastructure.NormalizeBicepIdentifier(Name)}_Id", Parent);

    /// <summary>
    /// Gets the parent Azure Virtual Network resource.
    /// </summary>
    public AzureVirtualNetworkResource Parent { get; }

    /// <summary>
    /// Gets or sets the NAT Gateway associated with the subnet.
    /// </summary>
    internal AzureNatGatewayResource? NatGateway { get; set; }

    /// <summary>
    /// Gets or sets the Network Security Group associated with the subnet.
    /// </summary>
    internal AzureNetworkSecurityGroupResource? NetworkSecurityGroup { get; set; }

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        => !string.IsNullOrEmpty(argument) ? argument : throw new ArgumentNullException(paramName);

    /// <summary>
    /// Converts the current instance to a provisioning entity.
    /// </summary>
    internal SubnetResource ToProvisioningEntity(AzureResourceInfrastructure infra, ProvisionableResource? dependsOn)
    {
        var subnet = new SubnetResource(Infrastructure.NormalizeBicepIdentifier(Name))
        {
            Name = SubnetName,
        };

        // Set the address prefix from either the literal string or the parameter
        if (_addressPrefix is string addressPrefix)
        {
            subnet.AddressPrefix = addressPrefix;
        }
        else if (_addressPrefix is ParameterResource addressPrefixParameter)
        {
            subnet.AddressPrefix = addressPrefixParameter.AsProvisioningParameter(infra);
        }
        else
        {
            throw new UnreachableException("AddressPrefix must be set either as a string or a ParameterResource.");
        }

        if (dependsOn is not null)
        {
            subnet.DependsOn.Add(dependsOn);
        }

        if (this.TryGetLastAnnotation<AzureSubnetServiceDelegationAnnotation>(out var serviceDelegationAnnotation))
        {
            subnet.Delegations.Add(new ServiceDelegation()
            {
                Name = serviceDelegationAnnotation.Name,
                ServiceName = serviceDelegationAnnotation.ServiceName
            });
        }

        if (NatGateway is not null)
        {
            // The NAT Gateway lives in a separate bicep module, so reference its ID via parameter
            subnet.NatGatewayId = NatGateway.Id.AsProvisioningParameter(infra);
        }

        if (NetworkSecurityGroup is not null)
        {
            // The NSG lives in a separate bicep module, so reference its ID via parameter
            subnet.NetworkSecurityGroup.Id = NetworkSecurityGroup.Id.AsProvisioningParameter(infra);
        }

        // add a provisioning output for the subnet ID so it can be referenced by other resources
        infra.Add(new ProvisioningOutput(Id.Name, typeof(string))
        {
            Value = subnet.Id
        });

        return subnet;
    }
}
