// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvus.Client;

namespace Aspire.Milvus.Client;
// TODO: Use health check from AspNetCore.Diagnostics.HealthChecks once it's implemented via this issue:
// https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks/issues/2214
internal sealed class MilvusHealthCheck : IHealthCheck
{
    private readonly MilvusClient _milvusClient;

    public MilvusHealthCheck(MilvusClient milvusClient)
    {
        ArgumentNullException.ThrowIfNull(milvusClient, nameof(milvusClient));
        _milvusClient = milvusClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var milvusHealthState = await _milvusClient.HealthAsync(cancellationToken).ConfigureAwait(false);

            return milvusHealthState.IsHealthy
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(HealthStatus.Unhealthy, description: $"Milvus server is unhealthy: {milvusHealthState}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Failed to connect to Milvus server.", ex);
        }
    }
}
