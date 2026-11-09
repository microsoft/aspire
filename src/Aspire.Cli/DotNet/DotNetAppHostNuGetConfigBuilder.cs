// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using NuGet.Configuration;

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
        var ambientPatterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in settings.PackageSourceMappings.GroupBy(
            static mapping => mapping.SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            ambientPatterns[group.Key] = [.. group.SelectMany(static mapping => mapping.Patterns)];
        }
        var ambientMapping = new PackageSourceMapping(ambientPatterns);
        // Introducing the first mapping makes every unmapped dependency ineligible for
        // restore. Keep mapping disabled rather than persisting ambient fallback policy.
        // https://learn.microsoft.com/nuget/consume-packages/package-source-mapping#package-pattern-requirements
        var mappings = settings.PackageSourceMappings.Count == 0
            ? []
            : integrationSources.Select(source => new NuGetPackageSourceMapping(source.Key,
                [.. integrationMappings
                    .Where(mapping => PackageSourceIdentity.Comparer.Equals(mapping.Source, source.Source))
                    .Select(static mapping => mapping.PackageFilter)
                    .Where(pattern => !IsAlreadyRoutedToIntegrationSource(settings, workloadId, ambientMapping, source, pattern))
                    .Distinct(StringComparer.OrdinalIgnoreCase)]))
                .Where(static mapping => mapping.Patterns.Count > 0)
                .ToArray();
        var selectedPatterns = selectedMappings.Count == 0
            ? [PackageSourceOverrideMappings.DefaultPackagePattern]
            : integrationMappings.Select(static mapping => mapping.PackageFilter)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedKeys = selectedSources.Select(static source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactIntegrationPatterns = integrationMappings
            .Where(static mapping => mapping.SourceKey is not null &&
                PackageSourceOverrideMappings.IsExactPackagePattern(mapping.PackageFilter))
            .Select(static mapping => mapping.PackageFilter)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preservedIdentities = preservedSources
            .Select(source => NuGetSourceIdentity.Compute(source, settings.SourceIdentityKey))
            .ToHashSet(StringComparer.Ordinal);
        var retiredKeys = new List<string>();
        var removedMappings = new List<NuGetPackageSourceMapping>();
        var isExactPackageSelection = selectedMappings.Count > 0 &&
            selectedMappings.All(static mapping => PackageSourceOverrideMappings.IsExactPackagePattern(mapping.PackageFilter));
        // Explicit named-feed selections replace that package's previous exact routing.
        // Other user patterns remain untouched; persistence checks that removals are local.
        foreach (var source in settings.Sources.Where(source =>
            (NuGetConfigurationBuilder.IsCliManagedSource(source, workloadId) ||
             source.IsIntegrationSource && exactIntegrationPatterns.Count > 0) &&
            !preservedIdentities.Contains(source.Identity)))
        {
            var isCliManaged = NuGetConfigurationBuilder.IsCliManagedSource(source, workloadId);
            var patterns = settings.PackageSourceMappings
                .Where(mapping => string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(static mapping => mapping.Patterns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var obsoletePatterns = patterns.Where(pattern =>
                selectedMappings.Count == 0
                ? NuGetConfigurationBuilder.IsObsoleteStableChannelPattern(source, pattern)
                : (isCliManaged ||
                 !selectedKeys.Contains(source.Name) && exactIntegrationPatterns.Contains(pattern)) &&
                (!source.IsIntegrationSource || selectedPatterns.Any(selectedPattern =>
                    PackageSourceOverrideMappings.IsExactPackagePattern(selectedPattern) &&
                    PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(pattern, selectedPattern))) &&
                selectedPatterns.Any(selectedPattern =>
                    PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(pattern, selectedPattern)) &&
                !mappings.Any(selected =>
                    string.Equals(selected.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase) &&
                    selected.Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))).ToArray();
            // Unmapped and catch-all SDK feeds remain necessary after an integration add.
            if (!isExactPackageSelection && !source.IsIntegrationSource && !selectedKeys.Contains(source.Name) &&
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

    private static bool IsAlreadyRoutedToIntegrationSource(
        NuGetSettingsInfo settings,
        string workloadId,
        PackageSourceMapping ambientMapping,
        NuGetConfigSource source,
        string packagePattern)
    {
        if (!source.IsAmbient || !source.IsEnabled ||
            !PackageSourceOverrideMappings.IsExactPackagePattern(packagePattern))
        {
            return false;
        }

        var ambientSource = settings.Sources.Single(ambient =>
            string.Equals(ambient.Name, source.Key, StringComparison.OrdinalIgnoreCase));
        // Channel aliases must participate in cleanup; explicitly named integration aliases
        // can retain sufficient routing without materializing another mapping.
        if (!ambientSource.IsIntegrationSource ||
            !source.IsExplicitlyNamed && NuGetConfigurationBuilder.IsCliManagedSource(ambientSource, workloadId))
        {
            return false;
        }

        // A matching wildcard is insufficient: exact IDs and longer prefixes take precedence.
        // Reuse policy only when every eligible enabled alias resolves to the selected feed.
        // https://learn.microsoft.com/nuget/consume-packages/package-source-mapping#package-pattern-precedence
        var keys = ambientMapping.GetConfiguredPackageSources(packagePattern);
        var eligibleSources = settings.Sources.Where(ambient =>
            ambient.IsEnabled && keys.Contains(ambient.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        return eligibleSources.Length > 0 &&
            eligibleSources.All(ambient => string.Equals(ambient.Identity, ambientSource.Identity, StringComparison.Ordinal));
    }
}

internal sealed record DotNetAppHostNuGetConfig(
    string WorkloadId,
    NuGetConfigOverlay SourceChanges,
    IReadOnlyList<string> SelectedSourceKeys,
    IReadOnlyList<NuGetPackageSourceMapping> PackageSourceMappings,
    IReadOnlyList<NuGetPackageSourceMapping> RemovedPackageSourceMappings);
