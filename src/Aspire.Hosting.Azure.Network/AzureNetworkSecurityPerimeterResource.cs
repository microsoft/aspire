// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Network Security Perimeter resource.
/// </summary>
/// <remarks>
/// <para>
/// A Network Security Perimeter groups PaaS resources (such as Storage, Key Vault, Cosmos DB, and SQL)
/// into a logical security boundary. Resources within the perimeter can communicate with each other,
/// while public access is controlled by the perimeter's access mode and rules.
/// </para>
/// <para>
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/>
/// to configure specific <see cref="Azure.Provisioning"/> properties.
/// </para>
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Network Security Perimeter resource.</param>
public class AzureNetworkSecurityPerimeterResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the "id" output reference from the Azure Network Security Perimeter resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    internal List<AzureNspAccessRule> AccessRules { get; } = [];

    internal List<NspAssociationConfig> Associations { get; } = [];

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        var existing = resources.OfType<NetworkSecurityPerimeter>().SingleOrDefault(r => r.BicepIdentifier == bicepIdentifier);

        if (existing is not null)
        {
            return existing;
        }

        var nsp = NetworkSecurityPerimeter.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, nsp))
        {
            nsp.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(nsp);
        return nsp;
    }

    internal sealed record NspAssociationConfig(
        string Name,
        BicepOutputReference TargetResourceId,
        NetworkSecurityPerimeterAssociationAccessMode AccessMode);
}
