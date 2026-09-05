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
public class RabbitMQShovelResource : RabbitMQProvisionableResource, IRabbitMQServerChild
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

    // The shovel's PUT references source and destination by name, so both must be declared on the broker
    // before the shovel can be created.
    internal override IEnumerable<RabbitMQProvisionableResource> HealthDependencies => [Source, Destination];

    /// <summary>
    /// Builds the shovel definition Aspire authors. Shared by <see cref="ReconcileAsync"/> (PUT) and
    /// <see cref="ProbeAsync"/> (drift comparison) so both sides use the exact same definition.
    /// </summary>
    /// <remarks>
    /// Shovels run inside the broker container and connect via the internal AMQP port, so the source and
    /// destination URIs are derived from the server's container URI (not the host-mapped connection string).
    /// </remarks>
    private async ValueTask<RabbitMQShovelDefinition> BuildDefinitionAsync(CancellationToken cancellationToken)
    {
        var server = Parent.Parent;
        var containerUri = await server.ContainerUriExpression.GetValueAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new DistributedApplicationException($"Could not resolve container URI for shovel '{ShovelName}'.");

        var vhostSuffix = Parent.VirtualHostName == "/"
            ? string.Empty
            : $"/{Uri.EscapeDataString(Parent.VirtualHostName)}";

        var brokerUri = containerUri + vhostSuffix;

        var ackModeString = AckMode switch
        {
            RabbitMQShovelAckMode.OnConfirm => "on-confirm",
            RabbitMQShovelAckMode.OnPublish => "on-publish",
            RabbitMQShovelAckMode.NoAck => "no-ack",
            _ => "on-confirm"
        };

        var value = new RabbitMQShovelDefinitionValue
        {
            SrcUri = brokerUri,
            SrcQueue = Source.Kind == RabbitMQDestinationKind.Queue ? Source.ProvisionedName : null,
            SrcExchange = Source.Kind == RabbitMQDestinationKind.Exchange ? Source.ProvisionedName : null,
            DestUri = brokerUri,
            DestQueue = Destination.Kind == RabbitMQDestinationKind.Queue ? Destination.ProvisionedName : null,
            DestExchange = Destination.Kind == RabbitMQDestinationKind.Exchange ? Destination.ProvisionedName : null,
            AckMode = ackModeString,
            ReconnectDelay = ReconnectDelay.HasValue ? (int)ReconnectDelay.Value.TotalSeconds : null,
            SrcDeleteAfter = SrcDeleteAfter?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return new RabbitMQShovelDefinition { Value = value };
    }

    internal override async ValueTask ReconcileAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var def = await BuildDefinitionAsync(cancellationToken).ConfigureAwait(false);
        await client.PutShovelAsync(Parent.VirtualHostName, ShovelName, def, cancellationToken).ConfigureAwait(false);
    }

    internal override async ValueTask DeleteAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => await client.DeleteShovelAsync(Parent.VirtualHostName, ShovelName, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var live = await client.GetShovelAsync(Parent.VirtualHostName, ShovelName, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return RabbitMQProbeResult.Unhealthy($"Shovel '{ShovelName}' does not exist in virtual host '{Parent.VirtualHostName}'.");
        }

        // Best-effort drift comparison of round-trippable fields only. Credentials embedded in src-uri /
        // dest-uri are redacted by the broker on read-back, so those URI fields cannot round-trip and are
        // intentionally NOT compared — comparing them would report false drift.
        var desired = await BuildDefinitionAsync(cancellationToken).ConfigureAwait(false);
        var d = desired.Value;
        var l = live.Value;

        return new RabbitMQDriftChecker("Shovel", ShovelName)
            .Field("ack-mode", d.AckMode, l.AckMode)
            .Field("reconnect-delay", d.ReconnectDelay, l.ReconnectDelay, nullDisplay: "(default)")
            .NullableField("src-queue", d.SrcQueue, l.SrcQueue, nullDisplay: "(none)")
            .NullableField("src-exchange", d.SrcExchange, l.SrcExchange, nullDisplay: "(none)")
            .NullableField("dest-queue", d.DestQueue, l.DestQueue, nullDisplay: "(none)")
            .NullableField("dest-exchange", d.DestExchange, l.DestExchange, nullDisplay: "(none)")
            // SrcDeleteAfter is serialized as "src-delete-after" and round-trips through the broker
            // (unlike src-uri/dest-uri whose credentials are redacted on read-back), so it is included
            // in the drift comparison.
            .NullableField("src-delete-after", d.SrcDeleteAfter, l.SrcDeleteAfter, nullDisplay: "(none)")
            .Result;
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => Parent;
}
