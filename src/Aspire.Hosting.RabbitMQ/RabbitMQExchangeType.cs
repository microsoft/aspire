// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the type of a RabbitMQ exchange.
/// </summary>
public enum RabbitMQExchangeType
{
    /// <summary>
    /// A direct exchange.
    /// </summary>
    Direct,

    /// <summary>
    /// A topic exchange.
    /// </summary>
    Topic,

    /// <summary>
    /// A fanout exchange.
    /// </summary>
    Fanout,

    /// <summary>
    /// A headers exchange.
    /// </summary>
    Headers
}
