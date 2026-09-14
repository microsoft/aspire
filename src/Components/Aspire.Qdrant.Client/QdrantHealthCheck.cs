// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qdrant.Client;

namespace Aspire.Qdrant.Client;
internal sealed class QdrantHealthCheck : IHealthCheck
{
    private readonly QdrantClient _client;

    public QdrantHealthCheck(QdrantClient client)
    {
        ArgumentNullException.ThrowIfNull(client, nameof(client));
        _client = client;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.HealthAsync(cancellationToken).ConfigureAwait(false);

            return response?.Title is not null
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(HealthStatus.Unhealthy, "Qdrant server returned an invalid health response.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Failed to connect to Qdrant server.", ex);
        }
    }
}
