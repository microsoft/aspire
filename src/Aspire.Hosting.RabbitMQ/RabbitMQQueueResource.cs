// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// A resource that represents a RabbitMQ queue.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, QueueName = {QueueName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQQueueResource : Resource, IResourceWithParent<RabbitMQVirtualHostResource>, IResourceWithConnectionString, IRabbitMQDestination, Provisioning.IRabbitMQProvisionable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQQueueResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue.</param>
    /// <param name="parent">The RabbitMQ virtual host resource associated with this queue.</param>
    public RabbitMQQueueResource(string name, string queueName, RabbitMQVirtualHostResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(queueName);
        ArgumentNullException.ThrowIfNull(parent);

        QueueName = queueName;
        Parent = parent;
    }

    /// <summary>
    /// Gets the name of the queue.
    /// </summary>
    public string QueueName { get; }

    /// <summary>
    /// Gets the parent RabbitMQ virtual host resource.
    /// </summary>
    public RabbitMQVirtualHostResource Parent { get; }

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
    /// Gets or sets the type of the queue.
    /// </summary>
    public RabbitMQQueueType QueueType { get; set; } = RabbitMQQueueType.Classic;

    /// <summary>
    /// Gets the arguments for the queue.
    /// </summary>
    public IDictionary<string, object?> Arguments { get; } = new Dictionary<string, object?>();

    string IRabbitMQDestination.EntityName => QueueName;
    RabbitMQVirtualHostResource IRabbitMQDestination.VirtualHost => Parent;
    RabbitMQDestinationKind IRabbitMQDestination.Kind => RabbitMQDestinationKind.Queue;

    /// <summary>
    /// Gets the connection string expression for the RabbitMQ queue.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.ConnectionStringExpression;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([
            new("QueueName", ReferenceExpression.Create($"{QueueName}")),
        ]);

    Task Provisioning.IRabbitMQProvisionable.ApplyAsync(Provisioning.IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ApplyAsync(client, cancellationToken);

    internal async Task ApplyAsync(Provisioning.IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>(Arguments);
        if (QueueType != RabbitMQQueueType.Classic)
        {
            args["x-queue-type"] = QueueType.ToString().ToLowerInvariant();
        }

        await client.DeclareQueueAsync(
            Parent.VirtualHostName,
            QueueName,
            Durable,
            Exclusive,
            AutoDelete,
            args.Count > 0 ? args : null,
            cancellationToken).ConfigureAwait(false);
    }
}
