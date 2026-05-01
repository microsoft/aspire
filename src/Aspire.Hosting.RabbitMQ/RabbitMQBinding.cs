// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// Represents a binding between a RabbitMQ exchange and a destination (queue or exchange).
/// </summary>
[AspireDto]
public sealed class RabbitMQBinding
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQBinding"/> class.
    /// </summary>
    /// <param name="destination">The destination of the binding.</param>
    /// <param name="routingKey">The routing key for the binding.</param>
    /// <param name="arguments">The arguments for the binding.</param>
    public RabbitMQBinding(IRabbitMQDestination destination, string routingKey, IDictionary<string, object?>? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(routingKey);

        Destination = destination;
        RoutingKey = routingKey;
        Arguments = arguments;
    }

    /// <summary>
    /// Gets the destination of the binding.
    /// </summary>
    public IRabbitMQDestination Destination { get; }

    /// <summary>
    /// Gets the routing key for the binding.
    /// </summary>
    public string RoutingKey { get; }

    /// <summary>
    /// Gets the arguments for the binding.
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; }
}
