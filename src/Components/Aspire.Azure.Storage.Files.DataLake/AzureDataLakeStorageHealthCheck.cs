// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Storage.Files.DataLake;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Azure.Storage.Files.DataLake;

/// <summary>
/// Represents the health check for Azure Data Lake Storage.
/// </summary>
/// <param name="dataLakeServiceClient">The <see cref="DataLakeServiceClient" /> used to perform Azure Data Lake Storage operations.</param>
internal sealed class AzureDataLakeStorageHealthCheck(DataLakeServiceClient dataLakeServiceClient) : IHealthCheck
{
    private readonly DataLakeServiceClient _dataLakeServiceClient =
        dataLakeServiceClient ?? throw new ArgumentNullException(nameof(dataLakeServiceClient));

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _dataLakeServiceClient.GetFileSystemsAsync(cancellationToken: cancellationToken)
                .AsPages(pageSizeHint: 1)
                .GetAsyncEnumerator(cancellationToken)
                .MoveNextAsync()
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: e);
        }
    }
}
