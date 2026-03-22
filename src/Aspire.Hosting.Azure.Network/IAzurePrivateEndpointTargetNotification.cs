// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Network;

/// <summary>
/// An optional interface that can be implemented by resources that are targets for
/// Azure private endpoints, to receive a notification when a private endpoint is created for them.
/// </summary>
public interface IAzurePrivateEndpointTargetNotification : IAzurePrivateEndpointTarget
{
    /// <summary>
    /// Handles the event that occurs when a new Azure private endpoint resource is created.
    /// </summary>
    /// <param name="privateEndpoint">The Azure private endpoint resource that was created. Cannot be null.</param>
    void OnPrivateEndpointCreated(IResourceBuilder<AzurePrivateEndpointResource> privateEndpoint);
}
