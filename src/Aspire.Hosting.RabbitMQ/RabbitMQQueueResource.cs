// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a RabbitMQ queue resource that is declared on the broker during provisioning.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, QueueName = {QueueName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQQueueResource : RabbitMQDestination, IResourceWithConnectionString, IResourceWithQueueArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQQueueResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue.</param>
    /// <param name="virtualHost">The RabbitMQ virtual host resource associated with this queue.</param>
    /// <param name="queueType">The type of the queue. Defaults to <see cref="RabbitMQQueueType.Classic"/>.</param>
    public RabbitMQQueueResource(string name, string queueName, RabbitMQVirtualHostResource virtualHost, RabbitMQQueueType queueType = RabbitMQQueueType.Classic) : base(name, virtualHost)
    {
        ArgumentNullException.ThrowIfNull(queueName);

        QueueName = queueName;
        QueueType = queueType;
    }

    /// <summary>
    /// Gets the name of the queue.
    /// </summary>
    public string QueueName { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the queue is durable.
    /// </summary>
    public bool Durable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the queue is exclusive.
    /// </summary>
    public bool Exclusive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the queue is auto-deleted.
    /// </summary>
    public bool AutoDelete { get; set; }

    /// <summary>
    /// Gets the type of the queue (classic, quorum, or stream). Set via the <c>type</c> parameter of <c>AddQueue</c>.
    /// </summary>
    public RabbitMQQueueType QueueType { get; }

    /// <summary>
    /// Gets the queue arguments for this queue declaration, such as TTL, length limits, and dead-lettering.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RabbitMQQueueExtensions.WithQueueArguments{T}"/> to configure these settings.
    /// For settings that should apply to multiple queues, use <c>AddPolicy</c> on the virtual host instead.
    /// </remarks>
    public RabbitMQQueueArguments QueueArguments { get; } = new();

    /// <inheritdoc/>
    public override string ProvisionedName => QueueName;

    /// <inheritdoc/>
    public override RabbitMQDestinationKind Kind => RabbitMQDestinationKind.Queue;

    /// <summary>
    /// Gets the connection string properties for this queue, including the queue name.
    /// </summary>
    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        VirtualHost.CombineProperties([
            new("QueueName", ReferenceExpression.Create($"{QueueName}")),
        ]);

    /// <summary>
    /// Gets the policies that apply to this queue, resolved at startup from matching <c>AddPolicy</c> calls.
    /// </summary>
    internal List<RabbitMQPolicyResource> AppliedPolicies { get; } = [];

    internal override IEnumerable<RabbitMQProvisionableResource> HealthDependencies
        // The dead-letter exchange must be provisioned before this queue's health is meaningful.
        => QueueArguments.DeadLetterExchange is { } dlx
            ? [.. AppliedPolicies, dlx]
            : [.. AppliedPolicies];

    // Compute once; used by both BuildDeclaredArguments (declare) and ProbeAsync (drift comparison).
    private string QueueTypeString => QueueType.ToString().ToLowerInvariant();

    /// <summary>
    /// Builds the declared queue arguments (the x-arguments Aspire authors) for this queue.
    /// Shared by <see cref="ReconcileAsync"/> (declare) and <see cref="ProbeAsync"/> (drift comparison)
    /// so both sides compare the exact same bag.
    /// </summary>
    private Dictionary<string, object?> BuildDeclaredArguments()
    {
        var args = new Dictionary<string, object?>();

        if (QueueType != RabbitMQQueueType.Classic)
        {
            args["x-queue-type"] = QueueTypeString;
        }

        QueueArguments.FlattenInto(args, $"Queue '{QueueName}'");
        return args;
    }

    internal override async ValueTask ReconcileAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var args = BuildDeclaredArguments();

        await client.DeclareQueueAsync(
            VirtualHost.VirtualHostName,
            QueueName,
            Durable,
            Exclusive,
            AutoDelete,
            args.Count > 0 ? args : null,
            cancellationToken).ConfigureAwait(false);
    }

    internal override async ValueTask DeleteAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => await client.DeleteQueueAsync(VirtualHost.VirtualHostName, QueueName, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var live = await client.GetQueueAsync(VirtualHost.VirtualHostName, QueueName, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return RabbitMQProbeResult.Unhealthy($"Queue '{QueueName}' does not exist in virtual host '{VirtualHost.VirtualHostName}'.");
        }

        // Compare only declared fields; server-computed fields (e.g. effective_policy_definition) are not mapped.
        var checker = new RabbitMQDriftChecker("Queue", QueueName);

        // Skip type comparison when the broker reports empty — older versions omit it for classic queues.
        if (!string.IsNullOrEmpty(live.Type))
        {
            checker.Field("type", QueueTypeString, live.Type);
        }

        return checker
            .Field("durable", Durable, live.Durable)
            .Field("exclusive", Exclusive, live.Exclusive)
            .Field("auto-delete", AutoDelete, live.AutoDelete)
            .Arguments($"Queue '{QueueName}'", BuildDeclaredArguments(), live.Arguments)
            .Result;
    }

    internal override Task BindAsync(IRabbitMQProvisioningClient client, string vhost, string sourceExchange, string routingKey, Dictionary<string, object?>? args, CancellationToken ct)
        => client.BindQueueAsync(vhost, sourceExchange, QueueName, routingKey, args, ct);
}
