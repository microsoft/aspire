// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal sealed class StaticGeneratedIntegrationIndexSource : IIntegrationIndexSource
{
    private static readonly IntegrationIndexDescriptor s_index = new(
        Id: "aspire",
        DisplayName: "Aspire",
        Provenance: "official",
        SourceKind: IntegrationIndexSourceKind.StaticGeneratedArtifact);

    private readonly IReadOnlyList<IntegrationEntry> _entries;

    public StaticGeneratedIntegrationIndexSource()
        : this([])
    {
    }

    internal StaticGeneratedIntegrationIndexSource(IEnumerable<IntegrationEntry> entries)
    {
        _entries = entries.ToArray();
    }

    public IntegrationIndexDescriptor Index => s_index;

    public async Task<IEnumerable<IntegrationPackageCandidate>> GetPackageCandidatesAsync(
        IntegrationIndexSourceContext context,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IntegrationPackageCandidate>();

        foreach (var entry in _entries)
        {
            foreach (var provider in entry.Providers.Where(static p => string.Equals(p.Type, IntegrationProviderTypes.NuGet, StringComparisons.CliInputOrOutput)))
            {
                foreach (var channel in context.Channels)
                {
                    var packages = await channel.GetPackagesAsync(provider.Package, context.WorkingDirectory, cancellationToken);
                    candidates.AddRange(packages.Select(package => new IntegrationPackageCandidate(entry, provider, package, channel)));
                }
            }
        }

        return candidates;
    }
}
