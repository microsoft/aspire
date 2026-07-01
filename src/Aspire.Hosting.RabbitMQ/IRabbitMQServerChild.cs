// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marker interface for resources that are children of a RabbitMQ server, reachable via a virtual host.
/// </summary>
internal interface IRabbitMQServerChild
{
    /// <summary>Gets the virtual host that owns this resource.</summary>
    RabbitMQVirtualHostResource VirtualHost { get; }
}
