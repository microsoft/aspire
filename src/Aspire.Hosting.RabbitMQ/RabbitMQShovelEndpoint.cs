// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an endpoint for a RabbitMQ shovel.
/// </summary>
[AspireDto]
public sealed class RabbitMQShovelEndpoint
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQShovelEndpoint"/> class.
    /// </summary>
    /// <param name="target">The target destination for the shovel endpoint.</param>
    public RabbitMQShovelEndpoint(IRabbitMQDestination target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
    }

    /// <summary>
    /// Gets the target destination for the shovel endpoint.
    /// </summary>
    public IRabbitMQDestination Target { get; }

    /// <summary>
    /// Gets the URI for the shovel endpoint.
    /// </summary>
    public ReferenceExpression GetUri() => Target.VirtualHost.ConnectionStringExpression;

    /// <summary>
    /// Gets the kind of the shovel endpoint.
    /// </summary>
    public RabbitMQDestinationKind Kind => Target.Kind;
}
