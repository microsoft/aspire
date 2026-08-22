// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Foundry;

internal sealed class FoundryLocalHealthCheck(FoundryResource resource, IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (resource.EmulatorServiceUri is null)
        {
            return HealthCheckResult.Unhealthy("Foundry Local has not reported an endpoint.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(resource.EmulatorServiceUri, "v1/models"));
            using var response = await httpClientFactory.CreateClient(nameof(FoundryLocalHealthCheck))
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return response.StatusCode is HttpStatusCode.OK
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Foundry Local returned HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException e)
        {
            return HealthCheckResult.Unhealthy("Foundry Local is not reachable.", e);
        }
    }
}
