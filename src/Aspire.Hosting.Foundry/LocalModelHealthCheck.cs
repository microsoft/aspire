// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Foundry;

internal sealed class LocalModelHealthCheck(FoundryDeploymentResource deployment, IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var modelId = deployment.LocalModelId;
        if (string.IsNullOrEmpty(modelId))
        {
            return HealthCheckResult.Unhealthy("Model has not been loaded.");
        }

        var endpoint = deployment.Parent.EmulatorServiceUri;
        if (endpoint is null)
        {
            return HealthCheckResult.Unhealthy("Foundry Local has not reported an endpoint.");
        }

        using var httpClient = httpClientFactory.CreateClient(nameof(LocalModelHealthCheck));
        if (!await FoundryLocalService.IsModelLoadedAsync(endpoint, modelId, httpClient, cancellationToken).ConfigureAwait(false))
        {
            return HealthCheckResult.Unhealthy("Model has not been loaded.");
        }

        return HealthCheckResult.Healthy();
    }
}
