// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Shared <see cref="IHealthCheck"/> implementation for all RabbitMQ child resources (virtual hosts, queues, exchanges, shovels, policies).
/// </summary>
/// <remarks>
/// The check proceeds in four stages: returns <see cref="HealthStatus.Degraded"/> while provisioning is in progress,
/// then awaits <see cref="IRabbitMQProvisionable.ProvisionedTask"/>, then awaits each
/// <see cref="IRabbitMQProvisionable.HealthDependencies"/> task, and finally calls
/// <see cref="IRabbitMQProvisionable.ProbeAsync"/> for a live broker verification.
/// </remarks>
internal sealed class RabbitMQProvisionableHealthCheck(IRabbitMQProvisionable self, IRabbitMQProvisioningClient client, ILogger<RabbitMQProvisionableHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Stage 1: return Degraded immediately if provisioning hasn't completed yet
        if (!self.ProvisionedTask.IsCompleted)
        {
            return HealthCheckResult.Degraded($"Provisioning of '{self.Name}' is in progress.");
        }

        // Stage 2: own provisioning
        try
        {
            await self.ProvisionedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Provisioning of '{self.Name}' failed: {ex.Message}";
            logger.LogWarning(ex, "{Message}", message);
            return HealthCheckResult.Unhealthy(message, ex);
        }

        // Stage 3: health dependencies (e.g. policies that apply to this queue/exchange)
        foreach (var dep in self.HealthDependencies)
        {
            try
            {
                await dep.ProvisionedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"Dependent resource '{dep.Name}' failed to provision: {ex.Message}";
                logger.LogWarning(ex, "{Message}", message);
                return HealthCheckResult.Unhealthy(message, ex);
            }
        }

        // Stage 4: live broker probe
        var probe = await self.ProbeAsync(client, cancellationToken).ConfigureAwait(false);
        if (!probe.IsHealthy)
        {
            logger.LogWarning("Health probe for '{Resource}' failed: {Reason}", self.Name, probe.Description);
            return HealthCheckResult.Unhealthy(probe.Description);
        }

        return HealthCheckResult.Healthy();
    }
}
