// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Integrations;

internal sealed class StaticGeneratedIntegrationIndexSource : IIntegrationIndexSource
{
    private const string EmbeddedAspireIndexResourceName = "integration-indexes/aspire.generated.json";

    private static readonly IntegrationIndexDescriptor s_index = new(
        Id: "aspire",
        DisplayName: "Aspire",
        Provenance: "official",
        SourceKind: IntegrationIndexSourceKind.StaticGeneratedArtifact);

    private readonly IReadOnlyList<IntegrationEntry> _entries;

    public StaticGeneratedIntegrationIndexSource()
        : this(LoadEmbeddedAspireIndex())
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
        var nugetProvidersByPackageId = _entries
            .SelectMany(static entry => entry.Providers
                .Where(static provider => string.Equals(provider.Type, IntegrationProviderTypes.NuGet, StringComparisons.CliInputOrOutput))
                .Select(provider => (Entry: entry, Provider: provider)))
            .ToLookup(static item => item.Provider.Package, StringComparers.NuGetPackageId);

        foreach (var channel in context.Channels)
        {
            var packages = await context.GetNuGetIntegrationPackagesAsync(channel, cancellationToken);
            foreach (var package in packages)
            {
                foreach (var (entry, provider) in nugetProvidersByPackageId[package.Id])
                {
                    candidates.Add(new IntegrationPackageCandidate(entry, provider, package, channel));
                }
            }
        }

        return candidates;
    }

    private static IReadOnlyList<IntegrationEntry> LoadEmbeddedAspireIndex()
    {
        var assembly = typeof(StaticGeneratedIntegrationIndexSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedAspireIndexResourceName)
            ?? throw new InvalidOperationException($"Could not find embedded integration index resource '{EmbeddedAspireIndexResourceName}' in assembly '{assembly.GetName().Name}'.");

        var artifact = JsonSerializer.Deserialize(stream, JsonSourceGenerationContext.Default.IntegrationIndexArtifact)
            ?? throw new InvalidOperationException($"Embedded integration index resource '{EmbeddedAspireIndexResourceName}' was empty.");

        if (artifact.SchemaVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported integration index schema version '{artifact.SchemaVersion}' in '{EmbeddedAspireIndexResourceName}'.");
        }

        var index = new IntegrationIndexDescriptor(
            artifact.Index.Id,
            artifact.Index.DisplayName,
            artifact.Index.Provenance,
            IntegrationIndexSourceKind.StaticGeneratedArtifact);

        return artifact.Entries.Select(entry => new IntegrationEntry
        {
            Index = index,
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Aliases = entry.Aliases,
            Providers = entry.Providers
                .Select(static provider => new IntegrationProviderReference(provider.Type, provider.Package))
                .ToArray()
        }).ToArray();
    }
}
