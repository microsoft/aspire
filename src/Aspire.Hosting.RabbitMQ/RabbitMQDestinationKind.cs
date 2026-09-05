// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies whether a RabbitMQ destination is a queue or an exchange.
/// </summary>
public enum RabbitMQDestinationKind
{
    /// <summary>
    /// The destination is a <see cref="RabbitMQQueueResource"/>.
    /// </summary>
    Queue,

    /// <summary>
    /// The destination is a <see cref="RabbitMQExchangeResource"/>.
    /// </summary>
    Exchange
}
