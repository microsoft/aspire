// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal sealed class NuGetSearchIntegrationIndexSource : IIntegrationIndexSource
{
    private static readonly IntegrationIndexDescriptor s_index = new(
        Id: "nuget-search",
        DisplayName: "NuGet search",
        Provenance: "third-party",
        SourceKind: IntegrationIndexSourceKind.DynamicNuGetSearch);

    public IntegrationIndexDescriptor Index => s_index;

    public async Task<IEnumerable<IntegrationPackageCandidate>> GetPackageCandidatesAsync(
        IntegrationIndexSourceContext context,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IntegrationPackageCandidate>();
        var candidatesLock = new object();

        await Parallel.ForEachAsync(context.Channels, cancellationToken, async (channel, ct) =>
        {
            var integrationPackages = await channel.GetIntegrationPackagesAsync(
                workingDirectory: context.WorkingDirectory,
                cancellationToken: ct);

            var channelCandidates = integrationPackages.Select(package =>
            {
                var provider = new IntegrationProviderReference(IntegrationProviderTypes.NuGet, package.Id);
                var entry = new IntegrationEntry
                {
                    Index = Index,
                    Id = IntegrationNameHelper.GenerateFriendlyName(package.Id),
                    DisplayName = package.Id,
                    Aliases = [package.Id],
                    Providers = [provider]
                };

                return new IntegrationPackageCandidate(entry, provider, package, channel);
            });

            lock (candidatesLock)
            {
                candidates.AddRange(channelCandidates);
            }
        });

        return candidates;
    }
}
