// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configures exchange-specific settings such as the alternate exchange for unroutable messages.
/// </summary>
/// <remarks>
/// Use <see cref="RabbitMQBuilderExtensions.WithExchangeArguments{T}"/> to configure these settings
/// on a <see cref="RabbitMQExchangeResource"/> or a <see cref="RabbitMQPolicyResource"/> that targets exchanges.
/// </remarks>
[AspireDto]
public sealed class RabbitMQExchangeArguments
{
    /// <summary>
    /// Gets the alternate exchange to which messages are routed when they cannot be
    /// delivered by this exchange (<c>alternate-exchange</c>).
    /// Use <see cref="RabbitMQBuilderExtensions.WithAlternateExchange{T}"/> to set this value.
    /// </summary>
    public RabbitMQExchangeResource? AlternateExchange { get; private set; }

    /// <summary>
    /// Gets additional exchange x-arguments not covered by the typed properties above.
    /// Do not repeat a key that already has a typed property (e.g. <c>alternate-exchange</c>); doing so will throw at startup.
    /// </summary>
    /// <remarks>
    /// Entries may be added until the application starts. Mutations after <see cref="BeforeStartEvent"/> are ignored.
    /// </remarks>
    public Dictionary<string, object?> AdditionalArguments { get; } = [];

    /// <summary>
    /// Sets the alternate exchange on this arguments object.
    /// Called by <see cref="RabbitMQBuilderExtensions.WithAlternateExchange{T}"/>.
    /// </summary>
    internal void SetAlternateExchange(RabbitMQExchangeResource ae)
    {
        AlternateExchange = ae;
    }

    internal const string XArgAlternateExchange = "alternate-exchange";

    internal static readonly FrozenSet<string> s_reservedKeys = new[]
    {
        XArgAlternateExchange,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Validates <see cref="AdditionalArguments"/> and merges all arguments into <paramref name="target"/>.
    /// </summary>
    /// <param name="target">The dictionary to merge into.</param>
    /// <param name="resourceDescription">Human-readable description of the resource (e.g. "Exchange 'orders'") for error messages.</param>
    /// <exception cref="Aspire.Hosting.DistributedApplicationException">
    /// Thrown when <see cref="AdditionalArguments"/> contains a key that is already handled by a typed property.
    /// </exception>
    internal void FlattenInto(IDictionary<string, object?> target, string resourceDescription)
    {
        foreach (var key in AdditionalArguments.Keys)
        {
            if (s_reservedKeys.Contains(key))
            {
                throw new DistributedApplicationException(
                    $"{resourceDescription}: '{key}' in AdditionalArguments is already handled by a typed property on {nameof(RabbitMQExchangeArguments)}. " +
                    $"Use the corresponding typed property instead.");
            }
        }

        foreach (var (k, v) in AdditionalArguments)
        {
            target[k] = v;
        }

        if (AlternateExchange is { } ae)
        {
            target[XArgAlternateExchange] = ae.ExchangeName;
        }
    }

    internal bool HasAnyValue() =>
        AlternateExchange is not null ||
        AdditionalArguments.Count > 0;
}
