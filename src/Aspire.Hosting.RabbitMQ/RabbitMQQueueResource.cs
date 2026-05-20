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
    {
        get
        {
            foreach (var policy in AppliedPolicies)
            {
                yield return policy;
            }

            // The dead-letter exchange must be provisioned before this queue's health is meaningful.
            if (QueueArguments.DeadLetterExchange is { } dlx)
            {
                yield return dlx;
            }
        }
    }

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var exists = await client.QueueExistsAsync(VirtualHost.VirtualHostName, QueueName, cancellationToken).ConfigureAwait(false);
        return exists
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Queue '{QueueName}' does not exist in virtual host '{VirtualHost.VirtualHostName}'.");
    }

    internal override Task BindAsync(IRabbitMQProvisioningClient client, string vhost, string sourceExchange, string routingKey, Dictionary<string, object?>? args, CancellationToken ct)
        => client.BindQueueAsync(vhost, sourceExchange, QueueName, routingKey, args, ct);
}
