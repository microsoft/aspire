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
            using var httpClient = httpClientFactory.CreateClient(nameof(FoundryLocalHealthCheck));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(resource.EmulatorServiceUri, "v1/models"));
            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                // The legacy service API predates GET /v1/models and exposes its readiness endpoint at
                // GET /openai/status. See https://github.com/microsoft/Foundry-Local/blob/v0.8.94/docs/reference/reference-rest.md#get-openaistatus.
                using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(resource.EmulatorServiceUri, "openai/status"));
                using var legacyResponse = await httpClient
                    .SendAsync(legacyRequest, cancellationToken)
                    .ConfigureAwait(false);

                return legacyResponse.StatusCode is HttpStatusCode.OK
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy($"Foundry Local returned HTTP {(int)legacyResponse.StatusCode}.");
            }

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
