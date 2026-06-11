// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Foundry;

internal sealed class FoundryLocalHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!FoundryLocalService.IsServiceRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Foundry Local not running"));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
