// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configures queue-specific x-arguments such as message TTL, length limits, and dead-lettering.
/// </summary>
/// <remarks>
/// Use <see cref="RabbitMQQueueExtensions.WithQueueArguments{T}"/> to configure these settings on a
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
    /// Use <see cref="RabbitMQQueueExtensions.WithDeadLetterExchange{T}"/> to set this value.
    /// </remarks>
    public RabbitMQExchangeResource? DeadLetterExchange { get; private set; }

    /// <summary>
    /// Gets the routing key used when dead-lettering messages (<c>x-dead-letter-routing-key</c>).
    /// </summary>
    /// <remarks>
    /// Use <see cref="RabbitMQQueueExtensions.WithDeadLetterExchange{T}"/> to set this value.
    /// </remarks>
    public string? DeadLetterRoutingKey { get; private set; }

    /// <summary>
    /// Gets additional queue x-arguments not covered by the typed properties above, such as <c>x-overflow</c>.
    /// </summary>
    /// <remarks>
    /// Do not repeat a key that already has a typed property (e.g. <c>x-message-ttl</c>); doing so will throw at startup.
    /// Entries may be added until the application starts. Mutations after <see cref="BeforeStartEvent"/> are ignored.
    /// </remarks>
    public Dictionary<string, object?> AdditionalArguments { get; init; } = [];

    /// <summary>Sets the dead-letter exchange and optional routing key; called by <see cref="RabbitMQQueueExtensions.WithDeadLetterExchange{T}"/>.</summary>
    internal void SetDeadLetterExchange(RabbitMQExchangeResource dlx, string? routingKey)
    {
        DeadLetterExchange = dlx;
        DeadLetterRoutingKey = routingKey;
    }

    // Queue x-argument keys. Policy definition keys use the same names without the "x-" prefix
    // (e.g. "x-message-ttl" → "message-ttl"); see ToPolicyKey.
    // RabbitMQ rejects policy definitions that use the "x-" prefix.
    // See: https://www.rabbitmq.com/docs/parameters#policies
    internal const string XArgMessageTtl = "x-message-ttl";
    internal const string XArgMaxLength = "x-max-length";
    internal const string XArgMaxLengthBytes = "x-max-length-bytes";
    internal const string XArgExpires = "x-expires";
    internal const string XArgDeadLetterExchange = "x-dead-letter-exchange";
    internal const string XArgDeadLetterRoutingKey = "x-dead-letter-routing-key";

    private static string ToPolicyKey(string xArgKey) => xArgKey[2..];

    internal static readonly FrozenSet<string> s_reservedKeys = new[]
    {
        XArgMessageTtl,
        XArgMaxLength,
        XArgMaxLengthBytes,
        XArgExpires,
        XArgDeadLetterExchange,
        XArgDeadLetterRoutingKey,
    }.ToFrozenSet(StringComparer.Ordinal);

    // Merges all arguments into target using AMQP x-argument keys (e.g. x-message-ttl).
    internal void FlattenInto(IDictionary<string, object?> target, string resourceDescription)
        => FlattenCore(target, resourceDescription, policyKeys: false);

    // Merges all arguments into target using policy definition keys (e.g. message-ttl, without the x- prefix).
    // RabbitMQ rejects policy definitions that use the x- prefix. See: https://www.rabbitmq.com/docs/parameters#policies
    internal void FlattenIntoPolicy(IDictionary<string, object?> target, string resourceDescription)
        => FlattenCore(target, resourceDescription, policyKeys: true);

    private void FlattenCore(IDictionary<string, object?> target, string resourceDescription, bool policyKeys)
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

        string Key(string xArgKey) => policyKeys ? ToPolicyKey(xArgKey) : xArgKey;

        if (MessageTtl is { } ttl) { target[Key(XArgMessageTtl)] = (long)ttl.TotalMilliseconds; }
        if (MaxLength is { } ml) { target[Key(XArgMaxLength)] = ml; }
        if (MaxLengthBytes is { } mlb) { target[Key(XArgMaxLengthBytes)] = mlb; }
        if (Expires is { } exp) { target[Key(XArgExpires)] = (long)exp.TotalMilliseconds; }
        if (DeadLetterExchange is { } dlx) { target[Key(XArgDeadLetterExchange)] = dlx.ExchangeName; }
        if (DeadLetterRoutingKey is { } drk) { target[Key(XArgDeadLetterRoutingKey)] = drk; }
    }

}
