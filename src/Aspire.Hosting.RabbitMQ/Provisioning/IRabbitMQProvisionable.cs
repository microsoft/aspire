// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Represents a RabbitMQ resource that can be provisioned against a live broker.
/// </summary>
internal interface IRabbitMQProvisionable
{
    /// <summary>
    /// Applies this resource to the broker using the supplied provisioning client.
    /// </summary>
    Task ApplyAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken);
}
