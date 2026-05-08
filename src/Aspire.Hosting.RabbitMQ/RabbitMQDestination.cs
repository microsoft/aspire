// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Base class for RabbitMQ destinations (queues and exchanges).
/// </summary>
/// <remarks>
/// <para>
/// The two concrete subtypes are <see cref="RabbitMQQueueResource"/> and
/// <see cref="RabbitMQExchangeResource"/>. The internal constructor prevents external
/// subclassing, keeping the hierarchy closed to this assembly.
/// </para>
/// <para>
/// Implements <see cref="IResourceWithConnectionString"/> so that the destination's
/// connection URI is available via <see cref="ConnectionStringExpression"/> without
/// requiring a separate wrapper type. The expression is forwarded from the parent
/// virtual host and is therefore always a <see cref="ReferenceExpression"/>.
/// </para>
/// </remarks>
public abstract class RabbitMQDestination : Resource,
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
    /// Gets the wire name of the entity as declared on the broker.
    /// </summary>
    /// <remarks>
    /// This is always a compile-time literal string. Queue and exchange names are plain
    /// <see langword="string"/> constructor parameters and cannot be driven by a
    /// <see cref="ParameterResource"/>. If parameterised names are ever added, all callers
    /// of this property in provisioning paths must be updated to use a
    /// <see cref="ReferenceExpression"/> instead.
    /// </remarks>
    public abstract string ProvisionedName { get; }

    /// <summary>
    /// Gets the kind of the destination.
    /// </summary>
    public abstract RabbitMQDestinationKind Kind { get; }

    /// <summary>
    /// Gets the connection string expression for this destination.
    /// Forwarded from the parent virtual host.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => VirtualHost.ConnectionStringExpression;

    /// <summary>
    /// Binds this destination to the given source exchange using the provisioning client.
    /// </summary>
    internal abstract Task BindAsync(
        IRabbitMQProvisioningClient client,
        string vhost,
        string sourceExchange,
        string routingKey,
        Dictionary<string, object?>? args,
        CancellationToken ct);
}
