// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a destination in RabbitMQ (a queue or an exchange).
/// </summary>
public interface IRabbitMQDestination
{
    /// <summary>
    /// Gets the name of the entity as known to the broker (the wire name).
    /// </summary>
    string ProvisionedName { get; }

    /// <summary>
    /// Gets the virtual host that contains the entity.
    /// </summary>
    RabbitMQVirtualHostResource VirtualHost { get; }

    /// <summary>
    /// Gets the kind of the destination.
    /// </summary>
    RabbitMQDestinationKind Kind { get; }
}

/// <summary>
/// Internal extension of <see cref="IRabbitMQDestination"/> that adds the ability to declare
/// a binding via the provisioning client. Only in-assembly resource types implement this interface.
/// </summary>
internal interface IRabbitMQBindableDestination : IRabbitMQDestination
{
    /// <summary>
    /// Binds this destination to the given source exchange using the provisioning client.
    /// </summary>
    Task BindAsync(IRabbitMQProvisioningClient client, string vhost, string sourceExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct);
}
