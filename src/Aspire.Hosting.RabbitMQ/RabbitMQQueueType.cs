// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// Represents the type of a RabbitMQ queue.
/// </summary>
public enum RabbitMQQueueType
{
    /// <summary>
    /// A classic queue.
    /// </summary>
    Classic,

    /// <summary>
    /// A quorum queue.
    /// </summary>
    Quorum,

    /// <summary>
    /// A stream queue.
    /// </summary>
    Stream
}
