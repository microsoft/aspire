// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a RabbitMQ dynamic shovel resource that moves messages from a source to a destination during provisioning.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ShovelName = {ShovelName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQShovelResource : RabbitMQProvisionableResource, IResourceWithParent<RabbitMQVirtualHostResource>, IRabbitMQServerChild
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQShovelResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="shovelName">The name of the shovel.</param>
    /// <param name="parent">The RabbitMQ virtual host resource associated with this shovel.</param>
    /// <param name="source">The source destination for the shovel.</param>
    /// <param name="destination">The destination for the shovel.</param>
    public RabbitMQShovelResource(string name, string shovelName, RabbitMQVirtualHostResource parent, RabbitMQDestination source, RabbitMQDestination destination) : base(name)
    {
        ArgumentNullException.ThrowIfNull(shovelName);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        ShovelName = shovelName;
        Parent = parent;
        Source = source;
        Destination = destination;
    }

    /// <summary>
    /// Gets the name of the shovel as known to the broker.
    /// </summary>
    public string ShovelName { get; }

    /// <summary>
    /// Gets the virtual host in which this shovel is defined.
    /// </summary>
    public RabbitMQVirtualHostResource Parent { get; }

    /// <summary>
    /// Gets the source queue or exchange from which messages are consumed.
    /// </summary>
    public RabbitMQDestination Source { get; }

    /// <summary>
    /// Gets the destination queue or exchange to which messages are forwarded.
    /// </summary>
    public RabbitMQDestination Destination { get; }

    /// <summary>
    /// Gets or sets the acknowledgment mode for the shovel. Defaults to <see cref="RabbitMQShovelAckMode.OnConfirm"/>.
    /// </summary>
    public RabbitMQShovelAckMode AckMode { get; set; } = RabbitMQShovelAckMode.OnConfirm;

    /// <summary>
    /// Gets or sets the reconnect delay for the shovel. When <see langword="null"/>, the broker default is used.
    /// </summary>
    public TimeSpan? ReconnectDelay { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages to transfer before the shovel is deleted.
    /// When <see langword="null"/>, the shovel runs indefinitely.
    /// </summary>
    public int? SrcDeleteAfter { get; set; }

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var state = await client.GetShovelStateAsync(Parent.VirtualHostName, ShovelName, cancellationToken).ConfigureAwait(false);
        return state == "running"
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Shovel '{ShovelName}' is in state '{state ?? "unknown"}'.");
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => Parent;
}
