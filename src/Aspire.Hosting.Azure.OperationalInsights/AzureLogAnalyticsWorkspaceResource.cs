// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure Log Analytics Workspace resource.
/// </summary>
/// <param name="name">The resource name.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Log Analytics Workspace resource.</param>
public class AzureLogAnalyticsWorkspaceResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IAzureNspAssociationTarget
{
    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// The identifier associated with the Azure Log Analytics Workspace resource.
    /// </summary>
    public BicepOutputReference Id => new("logAnalyticsWorkspaceId", this);

    /// <summary>
    /// Gets the "logAnalyticsWorkspaceId" output reference for the Azure Log Analytics Workspace resource.
    /// </summary>
    public BicepOutputReference WorkspaceId => new("logAnalyticsWorkspaceId", this);

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();
        
        // Check if an OperationalInsightsWorkspace with the same identifier already exists
        var existingStore = resources.OfType<OperationalInsightsWorkspace>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);
        
        if (existingStore is not null)
        {
            return existingStore;
        }
        
        // Create and add new resource if it doesn't exist
        var store = OperationalInsightsWorkspace.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            store))
        {
            store.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(store);
        return store;
    }
}
