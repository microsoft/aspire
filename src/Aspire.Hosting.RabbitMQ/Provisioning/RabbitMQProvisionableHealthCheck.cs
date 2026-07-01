// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Shared <see cref="IHealthCheck"/> implementation for all RabbitMQ child resources (virtual hosts, queues, exchanges, shovels, policies).
/// </summary>
/// <remarks>
/// The check proceeds in stages: returns <see cref="HealthStatus.Unhealthy"/> if the resource's Aspire state
/// is not yet <c>Running</c>, then checks each resource's health dependencies are healthy,
/// and finally calls the live broker probe for verification.
/// </remarks>
internal sealed class RabbitMQProvisionableHealthCheck(
    RabbitMQProvisionableResource self,
    IRabbitMQProvisioningClient client,
    ResourceNotificationService notifications,
    ILogger<RabbitMQProvisionableHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Stage 1: ensure this resource itself has reached the Running state before probing.
        // Without this guard, the health check would attempt live broker calls for resources
        // that haven't been provisioned yet, producing confusing errors in the dashboard.
        if (!notifications.TryGetCurrentState(self.Name, out var selfEvt) ||
            selfEvt.Snapshot.State?.Text != KnownResourceStates.Running)
        {
            return HealthCheckResult.Unhealthy($"'{self.Name}' is not yet Running.");
        }

        foreach (var dep in self.HealthDependencies)
        {
            if (!notifications.TryGetCurrentState(dep.Name, out var depEvt) || depEvt.Snapshot.HealthStatus != HealthStatus.Healthy)
            {
                return HealthCheckResult.Unhealthy($"Dependency '{dep.Name}' is not yet healthy.");
            }
        }

        RabbitMQProbeResult probe;
        try
        {
            probe = await self.ProbeAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health probe for '{Resource}' threw.", self.Name);
            return HealthCheckResult.Unhealthy(ex.Message);
        }

        if (!probe.IsHealthy)
        {
            logger.LogWarning("Health probe for '{Resource}' failed: {Reason}", self.Name, probe.Description);
            return HealthCheckResult.Unhealthy(probe.Description);
        }

        return HealthCheckResult.Healthy();
    }
}
