// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configures queue-specific x-arguments such as message TTL, length limits, and dead-lettering.
/// </summary>
/// <remarks>
/// Use <see cref="RabbitMQBuilderExtensions.WithQueueArguments{T}"/> to configure these settings on a
/// <see cref="RabbitMQQueueResource"/> or a <see cref="RabbitMQPolicyResource"/> that targets queues.
/// </remarks>
[AspireDto]
public sealed class RabbitMQQueueArguments
{
    /// <summary>
    /// Gets or sets the per-message TTL for the queue (<c>x-message-ttl</c>).
    /// </summary>
    public TimeSpan? MessageTtl { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages the queue will hold (<c>x-max-length</c>).
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// Gets or sets the maximum total size in bytes of all messages the queue will hold (<c>x-max-length-bytes</c>).
    /// </summary>
    public long? MaxLengthBytes { get; set; }

    /// <summary>
    /// Gets or sets how long a queue can remain unused before it is deleted (<c>x-expires</c>).
    /// </summary>
    public TimeSpan? Expires { get; set; }

    /// <summary>
    /// Gets the dead-letter exchange for this queue (<c>x-dead-letter-exchange</c>).
    /// </summary>
    /// <remarks>
    /// Use <see cref="RabbitMQBuilderExtensions.WithDeadLetterExchange{T}"/> to set this value.
    /// </remarks>
    public RabbitMQExchangeResource? DeadLetterExchange { get; private set; }

    /// <summary>
    /// Gets the routing key used when dead-lettering messages (<c>x-dead-letter-routing-key</c>).
    /// </summary>
    /// <remarks>
    /// Use <see cref="RabbitMQBuilderExtensions.WithDeadLetterExchange{T}"/> to set this value.
    /// </remarks>
    public string? DeadLetterRoutingKey { get; private set; }

    /// <summary>
    /// Gets additional queue x-arguments not covered by the typed properties above, such as <c>x-overflow</c>.
    /// </summary>
    /// <remarks>
    /// Do not repeat a key that already has a typed property (e.g. <c>x-message-ttl</c>); doing so will throw at startup.
    /// Entries may be added until the application starts. Mutations after <see cref="BeforeStartEvent"/> are ignored.
    /// </remarks>
    public Dictionary<string, object?> AdditionalArguments { get; } = [];

    /// <summary>Sets the dead-letter exchange and optional routing key; called by <see cref="RabbitMQBuilderExtensions.WithDeadLetterExchange{T}"/>.</summary>
    internal void SetDeadLetterExchange(RabbitMQExchangeResource dlx, string? routingKey)
    {
        DeadLetterExchange = dlx;
        DeadLetterRoutingKey = routingKey;
    }

    internal const string XArgMessageTtl = "x-message-ttl";
    internal const string XArgMaxLength = "x-max-length";
    internal const string XArgMaxLengthBytes = "x-max-length-bytes";
    internal const string XArgExpires = "x-expires";
    internal const string XArgDeadLetterExchange = "x-dead-letter-exchange";
    internal const string XArgDeadLetterRoutingKey = "x-dead-letter-routing-key";

    internal static readonly FrozenSet<string> s_reservedKeys = new[]
    {
        XArgMessageTtl,
        XArgMaxLength,
        XArgMaxLengthBytes,
        XArgExpires,
        XArgDeadLetterExchange,
        XArgDeadLetterRoutingKey,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Validates <see cref="AdditionalArguments"/> and merges all arguments into <paramref name="target"/>.</summary>
    /// <param name="target">The dictionary to merge into.</param>
    /// <param name="resourceDescription">Human-readable resource description (e.g. <c>Queue 'orders'</c>) used in error messages.</param>
    /// <exception cref="DistributedApplicationException">Thrown when <see cref="AdditionalArguments"/> contains a key already handled by a typed property.</exception>
    internal void FlattenInto(IDictionary<string, object?> target, string resourceDescription)
    {
        foreach (var key in AdditionalArguments.Keys)
        {
            if (s_reservedKeys.Contains(key))
            {
                throw new DistributedApplicationException(
                    $"{resourceDescription}: '{key}' in AdditionalArguments is already handled by a typed property on {nameof(RabbitMQQueueArguments)}. " +
                    $"Use the corresponding typed property instead.");
            }
        }

        foreach (var (k, v) in AdditionalArguments)
        {
            target[k] = v;
        }

        if (MessageTtl is { } ttl)
        {
            target[XArgMessageTtl] = (long)ttl.TotalMilliseconds;
        }

        if (MaxLength is { } ml)
        {
            target[XArgMaxLength] = ml;
        }

        if (MaxLengthBytes is { } mlb)
        {
            target[XArgMaxLengthBytes] = mlb;
        }

        if (Expires is { } exp)
        {
            target[XArgExpires] = (long)exp.TotalMilliseconds;
        }

        if (DeadLetterExchange is { } dlx)
        {
            target[XArgDeadLetterExchange] = dlx.ExchangeName;
        }

        if (DeadLetterRoutingKey is { } drk)
        {
            target[XArgDeadLetterRoutingKey] = drk;
        }
    }

}
