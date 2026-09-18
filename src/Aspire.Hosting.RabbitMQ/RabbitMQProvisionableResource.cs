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
    /// Shared reconcile coordinator used by both the parent-lifecycle event handlers and the manual
    /// Start/Stop/Restart command handlers. Storing it here (rather than as a closure variable) ensures
    /// both extension methods reach the SAME gate instance for the same child resource, so a parent
    /// <c>ResourceReadyEvent</c> can cancel a command's in-flight reconcile and vice-versa.
    /// </summary>
    internal RabbitMQReconcileGate ReconcileGate { get; } = new();

    /// <summary>
    /// Returns the set of other provisionable resources that must complete successfully before this resource's health check reports Healthy.
    /// </summary>
    internal virtual IEnumerable<RabbitMQProvisionableResource> HealthDependencies => [];

    /// <summary>
    /// Applies this resource's desired state to the live broker (the create/apply path). Each child owns how it declares itself.
    /// </summary>
    /// <remarks>
    /// Drives both the parent <c>ResourceReadyEvent</c> reconcile and the Start/Restart lifecycle commands.
    /// The default implementation is a no-op so resources that have nothing to provision opt in explicitly.
    /// </remarks>
    internal virtual ValueTask ReconcileAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Removes this resource's broker entity (the delete path). Each child owns how it deletes itself.
    /// </summary>
    /// <remarks>
    /// Drives the destructive Stop/Restart lifecycle commands, where Stop is defined as a delete of the broker entity.
    /// The default implementation is a no-op so resources that have nothing to delete opt in explicitly.
    /// </remarks>
    internal virtual ValueTask DeleteAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Performs a live broker probe to verify that this resource exists and is in the expected state.
    /// </summary>
    internal virtual ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ValueTask.FromResult(RabbitMQProbeResult.Healthy);
}
