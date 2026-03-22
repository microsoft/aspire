// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.AppConfiguration;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents Azure App Configuration.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureAppConfigurationResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure),
    IResourceWithConnectionString, IResourceWithEndpoints, IAzurePrivateEndpointTarget
{
    private EndpointReference EmulatorEndpoint => new(this, "emulator");

    /// <summary>
    /// Gets a value indicating whether the Azure App Configuration resource is running in the local emulator.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    /// <summary>
    /// Gets the appConfigEndpoint output reference for the Azure App Configuration resource.
    /// </summary>
    public BicepOutputReference Endpoint => new("appConfigEndpoint", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the "id" output reference for the resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure App Configuration resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
       IsEmulator
        ? ReferenceExpression.Create($"Endpoint={EmulatorEndpoint.Property(EndpointProperty.Url)};Id=anonymous;Secret=abcdefghijklmnopqrstuvwxyz1234567890;Anonymous=True")
        : ReferenceExpression.Create($"{Endpoint}");

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if an AppConfigurationStore with the same identifier already exists
        var existingStore = resources.OfType<AppConfigurationStore>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = AppConfigurationStore.FromExisting(bicepIdentifier);

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

    BicepOutputReference IAzurePrivateEndpointTarget.Id => Id;

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["configurationStores"];

    string IAzurePrivateEndpointTarget.GetPrivateDnsZoneName() => "privatelink.azconfig.io";
}
