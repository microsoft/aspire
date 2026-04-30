// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// Represents the kind of a RabbitMQ destination.
/// </summary>
public enum RabbitMQDestinationKind
{
    /// <summary>
    /// A queue destination.
    /// </summary>
    Queue,

    /// <summary>
    /// An exchange destination.
    /// </summary>
    Exchange
}
