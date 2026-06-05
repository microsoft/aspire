// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.SignalR;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an Azure SignalR resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureSignalRResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithConnectionString, IResourceWithEndpoints
{
    internal EndpointReference EmulatorEndpoint => new(this, "emulator");

    /// <summary>
    /// Gets a value indicating whether the Azure SignalR resource is running in the local emulator.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    /// <summary>
    /// Gets the "connectionString" output reference from the bicep template for Azure SignalR.
    /// </summary>
    public BicepOutputReference HostName => new("hostName", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the endpoint URI expression for the SignalR service.
    /// </summary>
    /// <remarks>
    /// In container mode (emulator), resolves to the container's endpoint URL.
    /// In Azure mode, resolves to the Azure SignalR service endpoint.
    /// Format: <c>https://{hostname}</c>.
    /// </remarks>
    public ReferenceExpression Endpoint =>
        IsEmulator ? 
            ReferenceExpression.Create($"{EmulatorEndpoint.Property(EndpointProperty.Url)}") : 
            ReferenceExpression.Create($"https://{HostName}");

    /// <summary>
    /// Gets the connection string template for the manifest for Azure SignalR.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        IsEmulator ?
            ReferenceExpression.Create($"Endpoint={EmulatorEndpoint.Property(EndpointProperty.Url)};AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;"):
            ReferenceExpression.Create($"Endpoint=https://{HostName};AuthType=azure");

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();
        
        // Check if a SignalRService with the same identifier already exists
        var existingStore = resources.OfType<SignalRService>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);
        
        if (existingStore is not null)
        {
            return existingStore;
        }
        
        // Create and add new resource if it doesn't exist
        var store = SignalRService.FromExisting(bicepIdentifier);

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

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", Endpoint);
    }
}
