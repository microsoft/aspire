// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a destination in RabbitMQ (a queue or an exchange).
/// </summary>
public interface IRabbitMQDestination
{
    /// <summary>
    /// Gets the name of the entity.
    /// </summary>
    string EntityName { get; }

    /// <summary>
    /// Gets the virtual host that contains the entity.
    /// </summary>
    RabbitMQVirtualHostResource VirtualHost { get; }

    /// <summary>
    /// Gets the kind of the destination.
    /// </summary>
    RabbitMQDestinationKind Kind { get; }
}
