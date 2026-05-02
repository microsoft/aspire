// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// A single <see cref="IHealthCheck"/> implementation shared by all RabbitMQ child resources
/// (virtual hosts, queues, exchanges, shovels). The check proceeds in three stages:
/// <list type="number">
///   <item>Await <see cref="IRabbitMQProvisionable.ProvisioningComplete"/> — unhealthy if faulted.</item>
///   <item>Await each <see cref="IRabbitMQProvisionable.HealthDependencies"/> TCS — unhealthy if any faulted.</item>
///   <item>Call <see cref="IRabbitMQProvisionable.ProbeAsync"/> for a live broker verification.</item>
/// </list>
/// </summary>
internal sealed class RabbitMQProvisionableHealthCheck(IRabbitMQProvisionable self, IRabbitMQProvisioningClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Stage 1: own provisioning
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
            return HealthCheckResult.Unhealthy($"Provisioning of '{self.Name}' failed: {ex.Message}", ex);
        }

        // Stage 2: health dependencies (e.g. policies that apply to this queue/exchange)
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
                return HealthCheckResult.Unhealthy(
                    $"Dependent resource '{dep.Name}' failed to provision: {ex.Message}", ex);
            }
        }

        // Stage 3: live broker probe
        var probe = await self.ProbeAsync(client, cancellationToken).ConfigureAwait(false);
        return probe.IsHealthy
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(probe.Description);
    }
}
