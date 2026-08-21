// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Expressions;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An Azure Provisioning <see cref="Infrastructure" /> which represents the root Bicep module that is generated for an Azure resource.
/// </summary>
/// <ats-summary>An Azure Provisioning <ats-see cref="!:type:Infrastructure" /> which represents the root Bicep module that is generated for an Azure resource.</ats-summary>
[AspireExport(ExposeProperties = true)]
public sealed class AzureResourceInfrastructure : Infrastructure
{
    internal AzureResourceInfrastructure(AzureProvisioningResource resource, string name) : base(name)
    {
        AspireResource = resource;

        if (resource.IsSubscriptionScopedInfrastructure)
        {
            TargetScope = DeploymentScope.Subscription;
        }

        // Always add a default location parameter.
        // azd assumes there will be a location parameter for every module.
        // The Infrastructure location resolver will resolve unset Location properties to this parameter.
        var location = new ProvisioningParameter("location", typeof(string))
        {
            Description = "The location for the resource(s) to be deployed."
        };
        if (!resource.IsSubscriptionScopedInfrastructure)
        {
            location.Value = BicepFunction.GetResourceGroup().Location;
        }
        Add(location);
    }

    /// <summary>
    /// The Aspire <see cref="AzureProvisioningResource"/> resource that this <see cref="AzureResourceInfrastructure"/> represents.
    /// </summary>
    public AzureProvisioningResource AspireResource { get; }

    internal IEnumerable<ProvisioningParameter> GetParameters() => GetProvisionableResources().OfType<ProvisioningParameter>();
}
