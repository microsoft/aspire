// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

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
    /// <param name="matchHeaders">
    /// The headers-exchange match arguments for the binding.
    /// Used when the source exchange is of type <see cref="RabbitMQExchangeType.Headers"/> to specify
    /// which message headers must match for the binding to be selected.
    /// </param>
    public RabbitMQBinding(RabbitMQDestination destination, string routingKey, Dictionary<string, object?>? matchHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(routingKey);

        Destination = destination;
        RoutingKey = routingKey;
        MatchHeaders = matchHeaders;
    }

    /// <summary>
    /// Gets the destination of the binding.
    /// </summary>
    public RabbitMQDestination Destination { get; }

    /// <summary>
    /// Gets the routing key for the binding.
    /// </summary>
    public string RoutingKey { get; }

    /// <summary>
    /// Gets the headers-exchange match arguments for the binding.
    /// </summary>
    /// <remarks>
    /// Used when the source exchange is of type <see cref="RabbitMQExchangeType.Headers"/> to specify
    /// which message headers must match for the binding to be selected.
    /// </remarks>
    public Dictionary<string, object?>? MatchHeaders { get; init; }
}
