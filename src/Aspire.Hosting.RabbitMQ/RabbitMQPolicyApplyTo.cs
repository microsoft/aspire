// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies which entity types a RabbitMQ policy applies to.
/// </summary>
public enum RabbitMQPolicyApplyTo
{
    /// <summary>
    /// The policy applies to queues only.
    /// </summary>
    Queues,

    /// <summary>
    /// The policy applies to exchanges only.
    /// </summary>
    Exchanges,

    /// <summary>
    /// The policy applies to both queues and exchanges.
    /// </summary>
    All,
}
