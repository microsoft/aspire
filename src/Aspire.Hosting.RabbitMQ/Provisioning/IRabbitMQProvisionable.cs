// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Implemented by RabbitMQ resources that can be provisioned against a live broker and verify their own health.
/// </summary>
internal interface IRabbitMQProvisionable
{
    /// <summary>Gets the resource name, used in health-check error messages.</summary>
    string Name { get; }

    /// <summary>
    /// Completes when this resource has been fully provisioned; faulted if provisioning failed.
    /// </summary>
    Task ProvisionedTask { get; }

    /// <summary>
    /// Applies this resource to the broker. Implementations must not throw; all failures are captured in <see cref="ProvisionedTask"/>.
    /// </summary>
    Task ApplyAsync(IRabbitMQProvisioningClient client, ResourceNotificationService notifications, ResourceLoggerService resourceLogger, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the set of other provisionable resources that must complete successfully before this resource's health check reports <c>Healthy</c>.
    /// Defaults to an empty sequence.
    /// </summary>
    IEnumerable<IRabbitMQProvisionable> HealthDependencies => [];

    /// <summary>
    /// Performs a live broker probe to verify that this resource exists and is in the expected state.
    /// Defaults to <see cref="RabbitMQProbeResult.Healthy"/> (no probe needed).
    /// </summary>
    ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ValueTask.FromResult(RabbitMQProbeResult.Healthy);
}
