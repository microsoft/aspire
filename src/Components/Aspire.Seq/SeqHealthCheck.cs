// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Seq;

/// <summary>
/// A diagnostic health check implementation for Seq servers.
/// </summary>
/// <param name="seqUri">The URI of the Seq server to check.</param>
internal sealed class SeqHealthCheck(string seqUri) : IHealthCheck
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { ActivityHeadersPropagator = null }) { BaseAddress = new Uri(seqUri) };
    private readonly string _displayHealthUri = new Uri(new Uri(seqUri), "/health").GetComponents(UriComponents.HttpRequestUrl, UriFormat.Unescaped);

    /// <summary>
    /// Checks the health of a Seq server by calling its <a href="https://docs.datalust.co/docs/using-the-http-api#checking-health">health</a> endpoint.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext _, CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            using var response = await _client.GetAsync("/health", cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Request to {_displayHealthUri} returned {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (TaskCanceledException tce) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Request to {_displayHealthUri} timed out", tce);
        }
        catch (TaskCanceledException tce) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Health check for {_displayHealthUri} was canceled", tce);
        }
        catch (HttpRequestException hre)
        {
            return HealthCheckResult.Unhealthy($"Failed to connect to {_displayHealthUri}", hre);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Health check failed for {_displayHealthUri}", ex);
        }
    }
}
