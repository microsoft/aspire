// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Azure;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.WebPubSub;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an Azure Web PubSub resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureWebPubSubResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithConnectionString, IAzurePrivateEndpointTarget
{
    internal Dictionary<string, AzureWebPubSubHubResource> Hubs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the "endpoint" output reference from the bicep template for Azure Web PubSub.
    /// </summary>
    public BicepOutputReference Endpoint => new("endpoint", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the "id" output reference for the resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the connection string template for the manifest for Azure Web PubSub.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"{Endpoint}");

    /// <summary>
    /// Gets the service endpoint URI expression for the Azure Web PubSub resource.
    /// </summary>
    /// <remarks>
    /// Format: <c>https://{name}.webpubsub.azure.com</c>.
    /// </remarks>
    public ReferenceExpression UriExpression => ReferenceExpression.Create($"{Endpoint}");

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a WebPubSubService with the same identifier already exists
        var existingStore = resources.OfType<WebPubSubService>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = WebPubSubService.FromExisting(bicepIdentifier);

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
        yield return new("Uri", UriExpression);
    }

    BicepOutputReference IAzurePrivateEndpointTarget.Id => Id;

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["webpubsub"];

    string IAzurePrivateEndpointTarget.GetPrivateDnsZoneName() => "privatelink.webpubsub.azure.com";
}
