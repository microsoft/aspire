// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// A resource that represents a RabbitMQ shovel.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ShovelName = {ShovelName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQShovelResource : Resource, IResourceWithParent<RabbitMQVirtualHostResource>, Provisioning.IRabbitMQProvisionable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQShovelResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="shovelName">The name of the shovel.</param>
    /// <param name="parent">The RabbitMQ virtual host resource associated with this shovel.</param>
    /// <param name="source">The source endpoint for the shovel.</param>
    /// <param name="destination">The destination endpoint for the shovel.</param>
    public RabbitMQShovelResource(string name, string shovelName, RabbitMQVirtualHostResource parent, RabbitMQShovelEndpoint source, RabbitMQShovelEndpoint destination) : base(name)
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
    /// Gets the name of the shovel.
    /// </summary>
    public string ShovelName { get; }

    /// <summary>
    /// Gets the parent RabbitMQ virtual host resource.
    /// </summary>
    public RabbitMQVirtualHostResource Parent { get; }

    /// <summary>
    /// Gets the source endpoint for the shovel.
    /// </summary>
    public RabbitMQShovelEndpoint Source { get; }

    /// <summary>
    /// Gets the destination endpoint for the shovel.
    /// </summary>
    public RabbitMQShovelEndpoint Destination { get; }

    /// <summary>
    /// Gets or sets the acknowledgment mode for the shovel.
    /// </summary>
    public RabbitMQShovelAckMode AckMode { get; set; } = RabbitMQShovelAckMode.OnConfirm;

    /// <summary>
    /// Gets or sets the reconnect delay for the shovel.
    /// </summary>
    public TimeSpan? ReconnectDelay { get; set; }

    /// <summary>
    /// Gets or sets the number of messages to transfer before deleting the shovel.
    /// </summary>
    public int? SrcDeleteAfter { get; set; }

    Task Provisioning.IRabbitMQProvisionable.ApplyAsync(Provisioning.IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ApplyAsync(client, cancellationToken);

    internal async Task ApplyAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var srcUri = await Source.GetUri().GetValueAsync(cancellationToken).ConfigureAwait(false);
        var destUri = await Destination.GetUri().GetValueAsync(cancellationToken).ConfigureAwait(false);

        if (srcUri is null)
        {
            throw new DistributedApplicationException($"Could not resolve source URI for shovel '{ShovelName}'.");
        }

        if (destUri is null)
        {
            throw new DistributedApplicationException($"Could not resolve destination URI for shovel '{ShovelName}'.");
        }

        var ackModeString = AckMode switch
        {
            RabbitMQShovelAckMode.OnConfirm => "on-confirm",
            RabbitMQShovelAckMode.OnPublish => "on-publish",
            RabbitMQShovelAckMode.NoAck => "no-ack",
            _ => "on-confirm"
        };

        var def = new RabbitMQShovelDefinitionValue
        {
            SrcUri = srcUri,
            SrcQueue = Source.Kind == RabbitMQDestinationKind.Queue ? Source.Target.EntityName : null,
            SrcExchange = Source.Kind == RabbitMQDestinationKind.Exchange ? Source.Target.EntityName : null,
            DestUri = destUri,
            DestQueue = Destination.Kind == RabbitMQDestinationKind.Queue ? Destination.Target.EntityName : null,
            DestExchange = Destination.Kind == RabbitMQDestinationKind.Exchange ? Destination.Target.EntityName : null,
            AckMode = ackModeString,
            ReconnectDelay = ReconnectDelay.HasValue ? (int)ReconnectDelay.Value.TotalSeconds : null,
            SrcDeleteAfter = SrcDeleteAfter?.ToString()
        };

        await client.PutShovelAsync(
            Parent.VirtualHostName,
            ShovelName,
            new RabbitMQShovelDefinition { Value = def },
            cancellationToken).ConfigureAwait(false);
    }
}
