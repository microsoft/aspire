// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Security.KeyVault.Keys;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Azure.Security.KeyVault;

internal sealed class AzureKeyVaultKeysHealthCheck : IHealthCheck
{
    private readonly KeyClient _client;

    public AzureKeyVaultKeysHealthCheck(KeyClient client)
        => _client = client;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var _ in _client.GetPropertiesOfKeysAsync(cancellationToken).AsPages(pageSizeHint: 1).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
