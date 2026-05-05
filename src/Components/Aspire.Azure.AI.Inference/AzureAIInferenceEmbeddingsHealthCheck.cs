// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.Inference;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Azure.AI.Inference;

internal sealed class AzureAIInferenceEmbeddingsHealthCheck : IHealthCheck
{
    private readonly EmbeddingsClient _client;

    public AzureAIInferenceEmbeddingsHealthCheck(EmbeddingsClient client)
        => _client = client;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.GetModelInfoAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
