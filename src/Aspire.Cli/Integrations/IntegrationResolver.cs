// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal interface IIntegrationResolver
{
    Task<IEnumerable<IntegrationPackageCandidate>> GetPackageCandidatesAsync(
        IntegrationIndexSourceContext context,
        CancellationToken cancellationToken);
}

internal sealed class IntegrationResolver(IEnumerable<IIntegrationIndexSource> indexSources) : IIntegrationResolver
{
    public async Task<IEnumerable<IntegrationPackageCandidate>> GetPackageCandidatesAsync(
        IntegrationIndexSourceContext context,
        CancellationToken cancellationToken)
    {
        var candidateSets = await Task.WhenAll(indexSources.Select(source => source.GetPackageCandidatesAsync(context, cancellationToken)));

        return candidateSets.SelectMany(static candidates => candidates);
    }
}
