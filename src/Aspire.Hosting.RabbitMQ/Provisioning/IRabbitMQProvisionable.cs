// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Represents a RabbitMQ resource that can be provisioned against a live broker
/// and that knows how to verify its own health after provisioning.
/// </summary>
internal interface IRabbitMQProvisionable
{
    /// <summary>Gets the resource name (used in health-check error messages).</summary>
    string Name { get; }

    /// <summary>
    /// Completed when this resource has been fully provisioned (or faulted if provisioning failed).
    /// Each resource owns its own TCS so failures are isolated to the affected resource.
    /// </summary>
    TaskCompletionSource ProvisioningComplete { get; }

    /// <summary>
    /// Applies this resource to the broker using the supplied provisioning client.
    /// </summary>
    Task ApplyAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the set of other provisionable resources that must have completed successfully
    /// before this resource's health check can report <c>Healthy</c>.
    /// Defaults to an empty sequence (no dependencies).
    /// </summary>
    IEnumerable<IRabbitMQProvisionable> HealthDependencies => [];

    /// <summary>
    /// Performs a live broker probe to verify that this resource exists and is in the expected state.
    /// Called by the health check after <see cref="ProvisioningComplete"/> and all
    /// <see cref="HealthDependencies"/> have completed successfully.
    /// Defaults to returning <see cref="RabbitMQProbeResult.Healthy"/> (no probe needed).
    /// </summary>
    ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ValueTask.FromResult(RabbitMQProbeResult.Healthy);
}
