// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Microsoft.Azure.Cosmos;

/// <summary>
/// A health check for Azure Cosmos DB that verifies the account is reachable by reading its
/// account properties. Modeled on the account-level probe in
/// AspNetCore.Diagnostics.HealthChecks' AzureCosmosDbHealthCheck.
/// </summary>
internal sealed class AzureCosmosDbHealthCheck : IHealthCheck
{
    private readonly CosmosClient _cosmosClient;

    public AzureCosmosDbHealthCheck(CosmosClient cosmosClient)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _cosmosClient = cosmosClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // ReadAccountAsync() doesn't accept a CancellationToken, so honor cancellation via WaitAsync.
            await _cosmosClient.ReadAccountAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
