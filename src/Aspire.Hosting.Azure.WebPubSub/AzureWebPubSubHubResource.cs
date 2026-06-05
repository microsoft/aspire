// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Azure.Provisioning.WebPubSub;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an Azure Web PubSub Hub setting resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="webpubsub">The <see cref="AzureWebPubSubResource"/> that the resource belongs to.</param>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, Hub = {HubName}")]
public class AzureWebPubSubHubResource(string name, AzureWebPubSubResource webpubsub) : Resource(name),
    IResourceWithParent<AzureWebPubSubResource>, IResourceWithConnectionString
{
    /// <summary>
    /// Represents an Azure Web PubSub Hub setting resource.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="hubName">The name of the Azure Web PubSub Hub.</param>
    /// <param name="webpubsub">The <see cref="AzureWebPubSubResource"/> that the resource belongs to.</param>
    public AzureWebPubSubHubResource(string name, string hubName, AzureWebPubSubResource webpubsub) : this(name, webpubsub)
    {
        HubName = hubName ?? throw new ArgumentNullException(nameof(hubName));
    }

    private readonly AzureWebPubSubResource _webpubsub = webpubsub ?? throw new ArgumentNullException(nameof(webpubsub));
    /// <summary>
    /// Gets the parent AzureWebPubSubResource of this AzureWebPubSubHubSettingResource.
    /// </summary>
    public AzureWebPubSubResource Parent => _webpubsub;

    /// <summary>
    /// Gets the name associated with the Azure Web PubSub Hub.
    /// </summary>
    public string HubName { get; set; } = name;

    /// <summary>
    /// Gets the connection string template for the manifest for Azure Web PubSub.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"Endpoint={Parent.Endpoint};Hub={HubName}");

    internal List<(ReferenceExpression url, string userEvents, string[]? systemEvents, UpstreamAuthSettings? auth)> EventHandlers { get; } = new();
}
