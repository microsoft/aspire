// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Expressions;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An Azure Provisioning <see cref="Infrastructure" /> which represents the root Bicep module that is generated for an Azure resource.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class AzureResourceInfrastructure : Infrastructure
{
    internal AzureResourceInfrastructure(AzureProvisioningResource resource, string name) : base(name)
    {
        AspireResource = resource;

        // Always add a default location parameter.
        // azd assumes there will be a location parameter for every module.
        // The Infrastructure location resolver will resolve unset Location properties to this parameter.
        Add(new ProvisioningParameter("location", typeof(string))
        {
            Description = "The location for the resource(s) to be deployed.",
            Value = BicepFunction.GetResourceGroup().Location
        });
    }

    /// <summary>
    /// The Aspire <see cref="AzureProvisioningResource"/> resource that this <see cref="AzureResourceInfrastructure"/> represents.
    /// </summary>
    public AzureProvisioningResource AspireResource { get; }

    internal IEnumerable<ProvisioningParameter> GetParameters() => GetProvisionableResources().OfType<ProvisioningParameter>();
}
