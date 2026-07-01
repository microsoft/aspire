// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Base class for RabbitMQ destinations (queues and exchanges).
/// </summary>
/// <remarks>
/// The two concrete subtypes are <see cref="RabbitMQQueueResource"/> and <see cref="RabbitMQExchangeResource"/>.
/// The connection string expression is forwarded from the parent virtual host.
/// </remarks>
public abstract class RabbitMQDestination : RabbitMQProvisionableResource,
    IResourceWithConnectionString,
    IResourceWithParent<RabbitMQVirtualHostResource>,
    IRabbitMQServerChild
{
    internal RabbitMQDestination(string name, RabbitMQVirtualHostResource virtualHost) : base(name)
    {
        ArgumentNullException.ThrowIfNull(virtualHost);
        VirtualHost = virtualHost;
    }

    /// <summary>
    /// Gets the virtual host that contains this destination.
    /// </summary>
    public RabbitMQVirtualHostResource VirtualHost { get; }

    /// <summary>
    /// Explicit implementation of <see cref="IResourceWithParent{T}.Parent"/> that returns <see cref="VirtualHost"/>.
    /// </summary>
    RabbitMQVirtualHostResource IResourceWithParent<RabbitMQVirtualHostResource>.Parent => VirtualHost;

    /// <summary>
    /// Gets the wire name of the entity as known to the broker.
    /// </summary>
    public abstract string ProvisionedName { get; }

    /// <summary>
    /// Gets the kind of the destination.
    /// </summary>
    public abstract RabbitMQDestinationKind Kind { get; }

    /// <summary>
    /// Gets the connection string expression for this destination, forwarded from the parent virtual host.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => VirtualHost.ConnectionStringExpression;

    /// <summary>
    /// Binds this destination to <paramref name="sourceExchange"/> using the provisioning client.
    /// </summary>
    internal abstract Task BindAsync(
        IRabbitMQProvisioningClient client,
        string vhost,
        string sourceExchange,
        string routingKey,
        Dictionary<string, object?>? args,
        CancellationToken ct);
}
