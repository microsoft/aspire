// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Microsoft.DurableTask.AzureManaged;

/// <summary>
/// A health check that pings the Durable Task Scheduler HTTP endpoint.
/// </summary>
internal sealed class DurableTaskSchedulerHealthCheck(string endpoint, string taskHubName) : IHealthCheck
{
    private readonly HttpClient _client = new(
        new SocketsHttpHandler { ActivityHeadersPropagator = null })
    {
        BaseAddress = new Uri(endpoint)
    };

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/taskhubs/ping");
            request.Headers.TryAddWithoutValidation("x-taskhub", taskHubName);

            using var response = await _client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Durable Task Scheduler ping returned {response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus, exception: ex);
        }
    }
}
