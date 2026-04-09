// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Azure.Search.Documents;

// TODO: Use health check from AspNetCore.Diagnostics.HealthChecks once it's implemented via this issue:
// https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks/issues/2156
internal sealed class AzureSearchIndexHealthCheck : IHealthCheck
{
    private readonly SearchIndexClient _searchIndexClient;

    public AzureSearchIndexHealthCheck(SearchIndexClient indexClient)
    {
        ArgumentNullException.ThrowIfNull(indexClient, nameof(indexClient));
        _searchIndexClient = indexClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await _searchIndexClient.GetServiceStatisticsAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Failed to connect to Azure AI Search service.", ex);
        }
    }
}
