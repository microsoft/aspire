// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.DotNet;

internal static class DotNetAppHostNuGetConfigBuilder
{
    public static NuGetConfiguration Build(
        NuGetSettingsInfo settings, string workloadId, PackageMapping[]? selectedMappings)
    {
        var operation = NuGetConfigurationBuilder.Build(
            settings, workloadId, selectedMappings, restrictToSelectedSources: false,
            packageScopedAppendSource: null, hasAuthoritativeAspirePolicy: true);

        return new NuGetConfiguration(settings, operation.ConfigSources, operation.Overlay)
        {
            AppHostConfig = selectedMappings is null
                ? null
                : Compose(settings, workloadId, selectedMappings, operation.ConfigSources, preservedSources: [])
        };
    }

    public static NuGetConfiguration BuildStable(
        NuGetSettingsInfo settings, string workloadId, PackageChannel channel)
    {
        var operation = NuGetConfigurationBuilder.BuildStableAppHostConfiguration(settings, workloadId, channel);

        return new NuGetConfiguration(settings, operation.ConfigSources, operation.Overlay)
        {
            AppHostConfig = Compose(settings, workloadId, selectedMappings: [], selectedSources: [],
                [.. (channel.Mappings ?? []).Select(static mapping => mapping.Source), PackageSources.NuGetOrg])
        };
    }

    private static DotNetAppHostNuGetConfig Compose(
        NuGetSettingsInfo settings,
        string workloadId,
        IReadOnlyList<PackageMapping> selectedMappings,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        IReadOnlyList<string> preservedSources)
    {
        var integrationMappings = selectedMappings
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .ToArray();
        var integrationSources = selectedSources
            .Where(source => integrationMappings.Any(mapping =>
                PackageSourceIdentity.Comparer.Equals(mapping.Source, source.Source)))
            .ToArray();
        // Introducing the first mapping makes every unmapped dependency ineligible for
        // restore. Keep mapping disabled rather than persisting ambient fallback policy.
        // https://learn.microsoft.com/nuget/consume-packages/package-source-mapping#package-pattern-requirements
        var mappings = settings.PackageSourceMappings.Count == 0
            ? []
            : integrationSources.Select(source => new NuGetPackageSourceMapping(source.Key,
                [.. integrationMappings
                    .Where(mapping => PackageSourceIdentity.Comparer.Equals(mapping.Source, source.Source))
                    .Select(static mapping => mapping.PackageFilter)
                    .Distinct(StringComparer.OrdinalIgnoreCase)]))
                .ToArray();
        var selectedPatterns = selectedMappings.Count == 0
            ? [PackageSourceOverrideMappings.DefaultPackagePattern]
            : integrationMappings.Select(static mapping => mapping.PackageFilter)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedKeys = selectedSources.Select(static source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preservedIdentities = preservedSources
            .Select(source => NuGetSourceIdentity.Compute(source, settings.SourceIdentityKey))
            .ToHashSet(StringComparer.Ordinal);
        var retiredKeys = new List<string>();
        var removedMappings = new List<NuGetPackageSourceMapping>();
        // Only CLI-owned channel entries are maintained. User mappings, including exact
        // Aspire package mappings, retain their native NuGet precedence.
        foreach (var source in settings.Sources.Where(source =>
            NuGetConfigurationBuilder.IsCliManagedSource(source, workloadId) &&
            !preservedIdentities.Contains(source.Identity)))
        {
            var patterns = settings.PackageSourceMappings
                .Where(mapping => string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(static mapping => mapping.Patterns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var obsoletePatterns = patterns.Where(pattern =>
                (selectedMappings.Count == 0 && pattern == PackageMapping.AllPackages ||
                 selectedPatterns.Any(selectedPattern =>
                     PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(pattern, selectedPattern))) &&
                !mappings.Any(selected =>
                    string.Equals(selected.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase) &&
                    selected.Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))).ToArray();
            if (!selectedKeys.Contains(source.Name) &&
                patterns.Except(obsoletePatterns, StringComparer.OrdinalIgnoreCase)
                    .All(static pattern => pattern == PackageMapping.AllPackages))
            {
                retiredKeys.Add(source.Name);
            }
            else if (obsoletePatterns.Length > 0)
            {
                removedMappings.Add(new NuGetPackageSourceMapping(source.Name, obsoletePatterns));
            }
        }
        var sourceChanges = new NuGetConfigOverlay(
            Sources: [.. integrationSources.Where(static source => !source.IsAmbient)
                .Select(static source => (source.Key, source.Source))],
            PackageSourceMappings: [],
            ClearDisabledPackageSources: false,
            DisabledPackageSourceKeys: [],
            GlobalPackagesFolder: null)
        {
            RetiredSourceKeys = retiredKeys
        };

        return new DotNetAppHostNuGetConfig(
            workloadId, sourceChanges, [.. integrationSources.Select(static source => source.Key)], mappings, removedMappings);
    }
}

internal sealed record DotNetAppHostNuGetConfig(
    string WorkloadId,
    NuGetConfigOverlay SourceChanges,
    IReadOnlyList<string> SelectedSourceKeys,
    IReadOnlyList<NuGetPackageSourceMapping> PackageSourceMappings,
    IReadOnlyList<NuGetPackageSourceMapping> RemovedPackageSourceMappings);
