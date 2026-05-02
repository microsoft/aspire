// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the acknowledgment mode for a RabbitMQ shovel.
/// </summary>
public enum RabbitMQShovelAckMode
{
    /// <summary>
    /// Acknowledge messages after they have been confirmed by the destination.
    /// </summary>
    OnConfirm,

    /// <summary>
    /// Acknowledge messages after they have been published to the destination.
    /// </summary>
    OnPublish,

    /// <summary>
    /// Do not acknowledge messages.
    /// </summary>
    NoAck
}
