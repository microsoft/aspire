// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Indicates that a RabbitMQ resource exposes exchange-specific arguments such as the alternate exchange
/// for unroutable messages.
/// </summary>
/// <remarks>
/// Implemented by <see cref="RabbitMQExchangeResource"/> and <see cref="RabbitMQPolicyResource"/>.
/// Use <see cref="RabbitMQBuilderExtensions.WithExchangeArguments{T}"/> or
/// <see cref="RabbitMQBuilderExtensions.WithAlternateExchange{T}"/> to configure these settings.
/// </remarks>
public interface IResourceWithExchangeArguments : IResource
{
    /// <summary>
    /// Gets the exchange arguments for this resource.
    /// </summary>
    RabbitMQExchangeArguments ExchangeArguments { get; }
}
