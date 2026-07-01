// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Base class for RabbitMQ resources that can be provisioned against a live broker and verify their own health.
/// </summary>
public abstract class RabbitMQProvisionableResource(string name) : Resource(name)
{
    /// <summary>
    /// Returns the set of other provisionable resources that must complete successfully before this resource's health check reports Healthy.
    /// </summary>
    internal virtual IEnumerable<RabbitMQProvisionableResource> HealthDependencies => [];

    /// <summary>
    /// Performs a live broker probe to verify that this resource exists and is in the expected state.
    /// </summary>
    internal virtual ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ValueTask.FromResult(RabbitMQProbeResult.Healthy);
}
