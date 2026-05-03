// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// A single <see cref="IHealthCheck"/> implementation shared by all RabbitMQ child resources
/// (virtual hosts, queues, exchanges, shovels, policies). The check proceeds in three stages:
/// <list type="number">
///   <item>If <see cref="IRabbitMQProvisionable.ProvisioningComplete"/> has not yet signalled, return <see cref="HealthStatus.Degraded"/> ("provisioning in progress").</item>
///   <item>Await <see cref="IRabbitMQProvisionable.ProvisioningComplete"/> — unhealthy if faulted.</item>
///   <item>Await each <see cref="IRabbitMQProvisionable.HealthDependencies"/> TCS — unhealthy if any faulted.</item>
///   <item>Call <see cref="IRabbitMQProvisionable.ProbeAsync"/> for a live broker verification.</item>
/// </list>
/// </summary>
internal sealed class RabbitMQProvisionableHealthCheck(IRabbitMQProvisionable self, IRabbitMQProvisioningClient client, ILogger<RabbitMQProvisionableHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Stage 1: return Degraded immediately if provisioning hasn't completed yet
        if (!self.ProvisioningComplete.Task.IsCompleted)
        {
            return HealthCheckResult.Degraded($"Provisioning of '{self.Name}' is in progress.");
        }

        // Stage 2: own provisioning
        try
        {
            await self.ProvisioningComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                await dep.ProvisioningComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
