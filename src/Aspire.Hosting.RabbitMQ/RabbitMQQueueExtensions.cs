// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for adding RabbitMQ queue resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RabbitMQQueueExtensions
{
    /// <summary>
    /// Adds a queue to a RabbitMQ virtual host.
    /// </summary>
    /// <param name="builder">The RabbitMQ virtual host resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue. Defaults to the resource name when not provided.</param>
    /// <param name="type">The type of the queue. Defaults to <see cref="RabbitMQQueueType.Classic"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds a queue to a RabbitMQ virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<RabbitMQQueueResource> AddQueue(
        this IResourceBuilder<RabbitMQVirtualHostResource> builder,
        [ResourceName] string name,
        string? queueName = null,
        RabbitMQQueueType type = RabbitMQQueueType.Classic)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (queueName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName, nameof(queueName));
        }

        var qName = queueName ?? name;
        if (builder.Resource.Queues.Any(q => q.QueueName == qName))
        {
            throw new DistributedApplicationException($"A queue with the name '{qName}' already exists in virtual host '{builder.Resource.VirtualHostName}'.");
        }

        var queue = new RabbitMQQueueResource(name, qName, builder.Resource, type);
        var vhost = builder.Resource;

        builder.Resource.Queues.Add(queue);

        return RabbitMQBuilderExtensions.WithProvisionableHealthCheck(builder.ApplicationBuilder.AddResource(queue))
            .WithIconName("MailInbox")
            .WithRabbitMQProvisioning(
                dependencies: [(vhost, WaitType.WaitUntilHealthy)],
                provisionAsync: async (q, client, _, ct) =>
                {
                    var args = new Dictionary<string, object?>();

                    if (q.QueueType != RabbitMQQueueType.Classic)
                    {
                        args["x-queue-type"] = q.QueueType.ToString().ToLowerInvariant();
                    }

                    q.QueueArguments.FlattenInto(args, $"Queue '{q.QueueName}'");

                    await client.DeclareQueueAsync(
                        vhost.VirtualHostName,
                        q.QueueName,
                        q.Durable,
                        q.Exclusive,
                        q.AutoDelete,
                        args.Count > 0 ? args : null,
                        ct).ConfigureAwait(false);
                });
    }

    /// <summary>
    /// Adds a queue to the default <c>/</c> virtual host of a RabbitMQ server.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue. Defaults to the resource name when not provided.</param>
    /// <param name="type">The type of the queue. Defaults to <see cref="RabbitMQQueueType.Classic"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds a queue to the default '/' virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("addQueueOnServer", MethodName = "addQueue")]
    public static IResourceBuilder<RabbitMQQueueResource> AddQueue(
        this IResourceBuilder<RabbitMQServerResource> builder,
        [ResourceName] string name,
        string? queueName = null,
        RabbitMQQueueType type = RabbitMQQueueType.Classic)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return RabbitMQBuilderExtensions.GetOrAddDefaultVirtualHost(builder).AddQueue(name, queueName, type);
    }

    /// <summary>
    /// Configures queue settings such as message TTL, length limits, and dead-letter routing.
    /// </summary>
    /// <typeparam name="T">
    /// The resource type. Accepts <see cref="RabbitMQQueueResource"/> (to set per-queue arguments)
    /// and <see cref="RabbitMQPolicyResource"/> (to apply the same settings via a broker policy).
    /// </typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">An action that sets properties on the <see cref="RabbitMQQueueArguments"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// Set a TTL and a maximum queue length on a queue:
    /// <code>
    /// vhost.AddQueue("orders")
    ///      .WithQueueArguments(a =>
    ///      {
    ///          a.MessageTtl = TimeSpan.FromMinutes(5);
    ///          a.MaxLength  = 10_000;
    ///      });
    /// </code>
    /// </example>
    /// <ats-summary>Configures typed queue x-arguments.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithQueueArguments<T>(
        this IResourceBuilder<T> builder,
        Action<RabbitMQQueueArguments> configure)
        where T : IResourceWithQueueArguments
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource.QueueArguments);
        return builder;
    }

    /// <summary>
    /// Routes dead-lettered messages from this queue to the specified exchange.
    /// </summary>
    /// <typeparam name="T">The resource type. Accepts <see cref="RabbitMQQueueResource"/> and <see cref="RabbitMQPolicyResource"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="dlx">The exchange that will receive dead-lettered messages.</param>
    /// <param name="routingKey">
    /// The routing key to use when republishing dead-lettered messages.
    /// When <see langword="null"/>, the original routing key of the message is preserved.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">
    /// Thrown when <paramref name="dlx"/> is in a different virtual host than the queue.
    /// </exception>
    /// <example>
    /// Send expired or rejected messages to a dedicated dead-letter exchange:
    /// <code>
    /// var dlx = vhost.AddExchange("dead-letters");
    ///
    /// vhost.AddQueue("orders")
    ///      .WithQueueArguments(a => a.MessageTtl = TimeSpan.FromMinutes(5))
    ///      .WithDeadLetterExchange(dlx);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Generic constraint uses IResourceWithParent<RabbitMQVirtualHostResource> which is not ATS-compatible. Use WithQueueArguments to set DeadLetterExchange directly in polyglot app hosts.")]
    public static IResourceBuilder<T> WithDeadLetterExchange<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RabbitMQExchangeResource> dlx,
        string? routingKey = null)
        where T : Resource, IResourceWithQueueArguments, IResourceWithParent<RabbitMQVirtualHostResource>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dlx);

        if (dlx.Resource.VirtualHost != ((IResourceWithParent<RabbitMQVirtualHostResource>)builder.Resource).Parent)
        {
            throw new DistributedApplicationException(
                $"Dead-letter exchange '{dlx.Resource.Name}' must be in the same virtual host as '{builder.Resource.Name}'.");
        }

        builder.Resource.QueueArguments.SetDeadLetterExchange(dlx.Resource, routingKey);
        return builder.WithRelationship(dlx.Resource, "DeadLetter");
    }

    /// <summary>
    /// Configures properties of a RabbitMQ queue.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withQueueProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQQueueResource> WithProperties(this IResourceBuilder<RabbitMQQueueResource> builder, Action<RabbitMQQueueResource> configure)
        => RabbitMQBuilderExtensions.WithPropertiesCore(builder, configure);
}
