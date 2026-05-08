// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marker interface for resources that are children of a RabbitMQ server, reachable via a virtual host.
/// </summary>
/// <remarks>
/// Implemented by <see cref="RabbitMQVirtualHostResource"/> (returns <c>this</c>),
/// <see cref="RabbitMQDestination"/> (queues and exchanges, returns the parent virtual host),
/// <see cref="RabbitMQShovelResource"/>, and <see cref="RabbitMQPolicyResource"/>.
/// Used internally to derive the server name for health-check registration without requiring
/// it to be passed explicitly at every call site.
/// </remarks>
internal interface IRabbitMQServerChild
{
    /// <summary>Gets the virtual host that owns this resource.</summary>
    RabbitMQVirtualHostResource VirtualHost { get; }
}
