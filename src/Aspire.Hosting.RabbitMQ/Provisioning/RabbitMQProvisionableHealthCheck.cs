// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Shared <see cref="IHealthCheck"/> implementation for all RabbitMQ child resources (virtual hosts, queues, exchanges, shovels, policies).
/// </summary>
internal sealed class RabbitMQProvisionableHealthCheck(
    RabbitMQProvisionableResource self,
    string serverName,
    IRabbitMQProvisioningClient client,
    ResourceNotificationService notifications,
    ILogger<RabbitMQProvisionableHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!notifications.TryGetCurrentState(serverName, out var serverEvt) ||
            serverEvt.Snapshot.State?.Text != KnownResourceStates.Running)
        {
            return HealthCheckResult.Unhealthy($"Broker server '{serverName}' is not Running.");
        }

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
