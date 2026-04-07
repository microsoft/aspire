// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Network Security Group resource.
/// </summary>
/// <remarks>
/// A Network Security Group contains security rules that control inbound and outbound network traffic.
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/>
/// to configure specific <see cref="Azure.Provisioning"/> properties.
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Network Security Group resource.</param>
public class AzureNetworkSecurityGroupResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the "id" output reference from the Azure Network Security Group resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutput => new("name", this);

    internal bool IsImplicitlyCreated { get; set; }

    internal List<AzureSecurityRule> SecurityRules { get; } = [];

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        var existing = resources.OfType<NetworkSecurityGroup>().SingleOrDefault(r => r.BicepIdentifier == bicepIdentifier);

        if (existing is not null)
        {
            return existing;
        }

        var nsg = NetworkSecurityGroup.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, nsg))
        {
            nsg.Name = NameOutput.AsProvisioningParameter(infra);
        }

        infra.Add(nsg);
        return nsg;
    }
}
