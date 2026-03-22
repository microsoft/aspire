// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Public IP Address resource.
/// </summary>
/// <remarks>
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/>
/// to configure specific <see cref="Azure.Provisioning"/> properties such as DNS labels, zones, or IP version.
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Public IP Address resource.</param>
public class AzurePublicIPAddressResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the "id" output reference from the Azure Public IP Address resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutput => new("name", this);

    /// <summary>
    /// Gets the "ipAddress" output reference containing the allocated IP address for the resource.
    /// </summary>
    public BicepOutputReference IpAddress => new("ipAddress", this);

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        var existing = resources.OfType<PublicIPAddress>().SingleOrDefault(r => r.BicepIdentifier == bicepIdentifier);

        if (existing is not null)
        {
            return existing;
        }

        var pip = PublicIPAddress.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, pip))
        {
            pip.Name = NameOutput.AsProvisioningParameter(infra);
        }

        infra.Add(pip);
        return pip;
    }
}
