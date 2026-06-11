// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Storage.Files.DataLake;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Azure.Storage.Files.DataLake;

/// <summary>
/// Represents the health check for Azure Data Lake Storage file system.
/// </summary>
/// <param name="dataLakeFileSystemClient">The <see cref="DataLakeFileSystemClient" /> used to perform Azure Data Lake Storage file system operations.</param>
internal sealed class AzureDataLakeFileSystemHealthCheck(DataLakeFileSystemClient dataLakeFileSystemClient) : IHealthCheck
{
    private readonly DataLakeFileSystemClient _dataLakeFileSystemClient =
        dataLakeFileSystemClient ?? throw new ArgumentNullException(nameof(dataLakeFileSystemClient));

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _dataLakeFileSystemClient.GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: e);
        }
    }
}
