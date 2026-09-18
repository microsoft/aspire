// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Indicates that a RabbitMQ resource exposes exchange-specific arguments such as the alternate exchange
/// for unroutable messages.
/// </summary>
public interface IResourceWithExchangeArguments : IResource
{
    /// <summary>
    /// Gets the exchange arguments for this resource.
    /// </summary>
    RabbitMQExchangeArguments ExchangeArguments { get; }
}
