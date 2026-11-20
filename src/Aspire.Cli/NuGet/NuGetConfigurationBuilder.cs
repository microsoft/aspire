// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Combines evaluated ambient NuGet settings with a selected package-source policy.
/// </summary>
internal static class NuGetConfigurationBuilder
{
    internal const string IntegrationSourceKeyPrefix = "aspire-integration-";

    public static NuGetConfiguration BuildStableAppHostConfiguration(
        NuGetSettingsInfo settings,
        string workloadId,
        PackageChannel channel)
    {
        var preservedSourceIdentities = (channel.Mappings ?? [])
            .Select(mapping => NuGetSourceIdentity.Compute(mapping.Source, settings.SourceIdentityKey))
            .Append(NuGetSourceIdentity.Compute(PackageSources.NuGetOrg, settings.SourceIdentityKey))
            .ToHashSet(StringComparer.Ordinal);
        // Shared .NET feeds are identified by their CLI aliases, including aliases left
        // behind when an AppHost moves and its path-derived workload identifier changes.
        var channelSources = settings.Sources
            .Where(source => !preservedSourceIdentities.Contains(source.Identity) &&
                IsCliManagedSource(source, workloadId))
            .ToDictionary(static source => source.Name, StringComparer.OrdinalIgnoreCase);

        // Stable returns to existing NuGet policy, not a prescribed public endpoint. Remove
        // channel-owned Aspire mappings while retaining user feeds and unrelated mappings.
        var ambientMappings = ResolveAmbientMappingAliases(settings);
        var mappings = ambientMappings
            .Select(mapping => channelSources.TryGetValue(mapping.SourceKey, out var source)
                ? mapping with
                {
                    Patterns = [.. mapping.Patterns.Where(pattern =>
                        !IsObsoleteStableChannelPattern(source, pattern))]
                }
                : mapping)
            .Where(static mapping => mapping.Patterns.Count > 0)
            .ToArray();
        foreach (var source in channelSources.Values.Where(static source => source.IsIntegrationSource))
        {
            if (!ambientMappings.Any(mapping =>
                string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase) &&
                mapping.Patterns.Any(pattern => IsObsoleteStableChannelPattern(source, pattern))))
            {
                continue;
            }

            // Neither discovery filters nor surviving narrower mappings establish the
            // full private dependency graph. Require manual cleanup before removing
            // broad routing from a shared SDK/integration feed.
            throw new ProjectUpdaterException(string.Format(
                CultureInfo.CurrentCulture, UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat,
                string.Join(", ", settings.ConfigPaths), source.Name));
        }
        var retiredSourceKeys = channelSources.Values
            .Where(source => !source.IsIntegrationSource && !mappings.Any(mapping =>
                string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(static source => source.Name)
            .ToArray();
        var overlay = new NuGetConfigOverlay(
            Sources: [],
            PackageSourceMappings: mappings,
            ClearDisabledPackageSources: retiredSourceKeys.Length > 0,
            DisabledPackageSourceKeys: [.. settings.DisabledPackageSourceKeys
                .Concat(retiredSourceKeys).Distinct(StringComparer.OrdinalIgnoreCase)],
            GlobalPackagesFolder: null)
        {
            ClearPackageSourceMappings = true,
            RetiredSourceKeys = retiredSourceKeys
        };

        return new NuGetConfiguration(settings, [], overlay);
    }

    public static NuGetConfiguration Build(
        NuGetSettingsInfo settings,
        string workloadId,
        PackageMapping[]? selectedMappings,
        bool restrictToSelectedSources,
        string? packageScopedAppendSource,
        bool hasAuthoritativeAspirePolicy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);
        if (restrictToSelectedSources && selectedMappings is not { Length: > 0 })
        {
            throw new ArgumentException("Source-restricted configuration requires a selected source.", nameof(selectedMappings));
        }

        var configSources = ResolveSourceAliases(
            selectedMappings,
            workloadId,
            settings.Sources,
            settings.ReservedPackageSourceKeys,
            settings.SourceIdentityKey);

        var overlay = selectedMappings is null
            ? null
            : ComposeOverlay(
                selectedMappings,
                settings,
                configSources,
                restrictToSelectedSources,
                globalPackagesFolder: null,
                packageScopedAppendSource,
                hasAuthoritativeAspirePolicy);

        return new NuGetConfiguration(
            settings,
            configSources,
            overlay);
    }

    internal static bool IsCliManagedSource(NuGetSourceInfo source, string workloadId)
        => source.IsCliManaged || IsCliManagedSourceKey(source.Name, workloadId);

    internal static bool IsObsoleteStableChannelPattern(NuGetSourceInfo source, string pattern)
        // Integration aliases own their mappings, not SDK policy. Shared channel
        // wildcards require manual cleanup because private dependencies may use them.
        => source.IsIntegrationSource
            ? !source.Name.StartsWith(IntegrationSourceKeyPrefix, StringComparison.OrdinalIgnoreCase) &&
                (pattern == PackageMapping.AllPackages ||
                 string.Equals(pattern, PackageSourceOverrideMappings.DefaultPackagePattern, StringComparison.OrdinalIgnoreCase))
            : pattern == PackageMapping.AllPackages ||
                PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(
                    pattern, PackageSourceOverrideMappings.DefaultPackagePattern);

    internal static bool IsCliManagedSourceKey(string sourceKey, string workloadId)
        => sourceKey.StartsWith(IntegrationSourceKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceKey, $"aspire-{PackageChannelNames.Daily}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceKey, $"aspire-{PackageChannelNames.Staging}", StringComparison.OrdinalIgnoreCase) ||
            sourceKey.StartsWith("aspire-pr-", StringComparison.OrdinalIgnoreCase) ||
            sourceKey.StartsWith("aspire-apphost-", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceKey, $"aspire-{workloadId}", StringComparison.OrdinalIgnoreCase) ||
            sourceKey.StartsWith($"aspire-{workloadId}-", StringComparison.OrdinalIgnoreCase);

    public static NuGetConfigSource[] ResolveSourceAliases(
        PackageMapping[]? mappings,
        string workloadId,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<string> reservedPackageSourceKeys,
        ReadOnlySpan<byte> sourceIdentityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        if (mappings is null)
        {
            return [];
        }

        var usedKeys = reservedPackageSourceKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedSourceKeyPrefix = $"aspire-{workloadId}";
        var sourceIndex = 0;
        var nextAdditionalSourceKey = 0;

        var resolvedSources = new List<NuGetConfigSource>();
        foreach (var source in mappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer))
        {
            // Source order still determines the primary source when its ambient alias is reused.
            var isPrimarySource = sourceIndex++ == 0;
            var sourceIdentity = NuGetSourceIdentity.Compute(source, sourceIdentityKey);
            var configuredKey = mappings.First(mapping => PackageSourceIdentity.Comparer.Equals(mapping.Source, source)).SourceKey;
            var preferredKey = configuredKey is null ? null : $"{IntegrationSourceKeyPrefix}{configuredKey}";
            if (configuredKey is not null)
            {
                SourceNameValidator.Validate(configuredKey, $"{IntegrationSourceConfiguration.SectionName}.{configuredKey}");
            }
            var ambientMatches = ambientSources
                .Where(candidate =>
                    string.Equals(candidate.Identity, sourceIdentity, StringComparison.Ordinal) ||
                    PackageSourceIdentity.IsNamedSourceReference(source) &&
                    string.Equals(candidate.Name, source, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (ambientMatches.Length > 0)
            {
                var enabledMatches = ambientMatches
                    .Where(static ambientSource => ambientSource.IsEnabled)
                    .ToArray();
                var selectedMatches = enabledMatches;
                if (selectedMatches.Length == 0)
                {
                    // Credentials and client certificates are attached to source aliases. When an
                    // equivalent source must be re-enabled, preserve the authenticated alias.
                    var preferredDisabledMatch = ambientMatches.FirstOrDefault(static ambientSource =>
                        ambientSource.HasCredentials || ambientSource.HasClientCertificates);
                    selectedMatches = [preferredDisabledMatch ?? ambientMatches[0]];
                }

                foreach (var ambientSource in selectedMatches)
                {
                    resolvedSources.Add(new NuGetConfigSource(
                        ambientSource.Name,
                        source,
                        IsAmbient: true,
                        ambientSource.IsEnabled)
                    {
                        IsExplicitlyNamed = string.Equals(ambientSource.Name, preferredKey, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ambientSource.Name, configuredKey, StringComparison.OrdinalIgnoreCase)
                    });
                }

                continue;
            }

            string key;
            if (preferredKey is not null)
            {
                key = preferredKey;
                if (!usedKeys.Add(key))
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.CurrentCulture, ErrorStrings.NuGetSourceKeyAlreadyInUse, key));
                }
            }
            else if (isPrimarySource)
            {
                key = generatedSourceKeyPrefix;
                if (!usedKeys.Add(key))
                {
                    do
                    {
                        key = $"{generatedSourceKeyPrefix}-{nextAdditionalSourceKey++}";
                    }
                    while (!usedKeys.Add(key));
                }
            }
            else
            {
                do
                {
                    key = $"{generatedSourceKeyPrefix}-{nextAdditionalSourceKey++}";
                }
                while (!usedKeys.Add(key));
            }

            resolvedSources.Add(new NuGetConfigSource(key, source, IsAmbient: false, IsEnabled: true)
            {
                IsExplicitlyNamed = preferredKey is not null
            });
        }

        return [.. resolvedSources];
    }

    public static NuGetConfigOverlay ComposeOverlay(
        PackageMapping[] selectedMappings,
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        bool restrictToSelectedSources,
        string? globalPackagesFolder,
        string? packageScopedAppendSource = null,
        bool hasAuthoritativeAspirePolicy = false)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var enabledSourceKeys = selectedSources
            .Where(static source => source.IsAmbient && !source.IsEnabled)
            .Select(static source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clearDisabledPackageSources = enabledSourceKeys.Count > 0;
        var disabledPackageSourceKeys = clearDisabledPackageSources
            ? settings.DisabledPackageSourceKeys
                .Where(key => !enabledSourceKeys.Contains(key))
                .ToArray()
            : [];
        var ambientMappings = ResolveAmbientMappingAliases(settings);
        var packageSourceMappings = ComposePackageSourceMappings(
            selectedMappings,
            ambientMappings,
            settings.Sources,
            selectedSources,
            packageScopedAppendSource,
            hasAuthoritativeAspirePolicy);

        if (restrictToSelectedSources)
        {
            // Search ignores mappings. Source-restricted operations must disable other aliases
            // without replacing the hierarchy that owns authentication and transport settings.
            var selectedKeys = selectedSources
                .Select(static source => source.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            clearDisabledPackageSources = true;
            disabledPackageSourceKeys = settings.Sources
                .Select(static source => source.Name)
                .Concat(settings.DisabledPackageSourceKeys)
                .Where(key => !selectedKeys.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // A disabled source's exact mapping can outrank the selected source's '*' mapping.
            // Remove its mappings as well so template installation can resolve from --source.
            packageSourceMappings = packageSourceMappings
                .Where(mapping => selectedKeys.Contains(mapping.SourceKey))
                .ToArray();
        }

        var selectedKeysForPolicy = selectedSources.Select(static source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Selecting an integration package does not replace the AppHost's SDK channel.
        var isExactPackageSelection = selectedMappings.Length > 0 &&
            selectedMappings.All(static mapping => PackageSourceOverrideMappings.IsExactPackagePattern(mapping.PackageFilter));
        var retiredSourceKeys = settings.Sources
            .Where(source =>
                !isExactPackageSelection &&
                !source.IsIntegrationSource &&
                !selectedKeysForPolicy.Contains(source.Name) &&
                !packageSourceMappings.Any(mapping =>
                    string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase) &&
                    mapping.Patterns.Any(static pattern => pattern != PackageMapping.AllPackages)) &&
                (source.IsCliManaged ||
                 !packageSourceMappings.Any(mapping => string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase)) &&
                 ambientMappings.Any(mapping => string.Equals(mapping.SourceKey, source.Name, StringComparison.OrdinalIgnoreCase))))
            .Select(static source => source.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (retiredSourceKeys.Count > 0)
        {
            // Retired CLI hives must not remain eligible for dependency lookup after a
            // channel change: they can disappear independently of the AppHost.
            packageSourceMappings = packageSourceMappings.Where(mapping => !retiredSourceKeys.Contains(mapping.SourceKey)).ToArray();
            clearDisabledPackageSources = true;
            disabledPackageSourceKeys =
            [
                .. settings.DisabledPackageSourceKeys.Where(key => !enabledSourceKeys.Contains(key)),
                .. disabledPackageSourceKeys,
                .. retiredSourceKeys
            ];
            disabledPackageSourceKeys = disabledPackageSourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        return new NuGetConfigOverlay(
            selectedSources
                .Where(static source => !source.IsAmbient)
                .Select(static source => (Key: source.Key, Source: source.Source))
                .ToArray(),
            packageSourceMappings,
            clearDisabledPackageSources,
            disabledPackageSourceKeys,
            globalPackagesFolder)
        {
            RetiredSourceKeys = [.. retiredSourceKeys]
        };
    }

    public static NuGetPackageSourceMapping[] ComposePackageSourceMappings(
        IReadOnlyList<PackageMapping> selectedMappings,
        IReadOnlyList<NuGetPackageSourceMapping> ambientMappings,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        string? packageScopedAppendSource = null,
        bool hasAuthoritativeAspirePolicy = false)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(ambientMappings);
        ArgumentNullException.ThrowIfNull(ambientSources);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var patternsBySourceKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hasPackageScopedAppendSource = packageScopedAppendSource is not null;

        if (ambientMappings.Count == 0)
        {
            // Enabling package-source mapping changes NuGet from "every enabled source can serve
            // every package" to deny-by-default. Reproduce that existing eligibility first.
            foreach (var ambientSource in ambientSources.Where(static source => source.IsEnabled))
            {
                AddPattern(patternsBySourceKey, ambientSource.Name, PackageMapping.AllPackages);
            }
        }
        else
        {
            foreach (var ambientMapping in ambientMappings)
            {
                foreach (var pattern in ambientMapping.Patterns)
                {
                    AddPattern(patternsBySourceKey, ambientMapping.SourceKey, pattern);
                }
            }
        }

        if (hasPackageScopedAppendSource && !hasAuthoritativeAspirePolicy)
        {
            // With no selected channel policy, ambient '*' mappings previously made those sources
            // eligible for Aspire packages. Promote that eligibility to the appended Aspire*
            // specificity so --source does not accidentally replace the ambient feeds.
            foreach (var (sourceKey, patterns) in patternsBySourceKey.ToArray())
            {
                if (patterns.Contains(PackageMapping.AllPackages, StringComparer.OrdinalIgnoreCase))
                {
                    AddPattern(
                        patternsBySourceKey,
                        sourceKey,
                        PackageSourceOverrideMappings.DefaultPackagePattern);
                }
            }
        }

        var authoritativePatterns = selectedMappings
            .Where(mapping =>
                mapping.PackageFilter != PackageMapping.AllPackages &&
                !(!hasAuthoritativeAspirePolicy &&
                  PackageSourceIdentity.Comparer.Equals(mapping.Source, packageScopedAppendSource) &&
                  string.Equals(
                      mapping.PackageFilter,
                      PackageSourceOverrideMappings.DefaultPackagePattern,
                      StringComparison.OrdinalIgnoreCase)))
            .Select(static mapping => mapping.PackageFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var authoritativeSources = selectedMappings
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .Select(static mapping => mapping.Source)
            .ToHashSet(PackageSourceIdentity.Comparer);
        foreach (var (sourceKey, patterns) in patternsBySourceKey)
        {
            var isIntegrationSource = ambientSources.Any(source =>
                source.IsIntegrationSource && string.Equals(source.Name, sourceKey, StringComparison.OrdinalIgnoreCase));
            // Channel changes must not take integration packages away from configured feeds.
            // An exact package selection can still move that package to a different source.
            patterns.RemoveAll(pattern => authoritativePatterns.Any(
                authoritativePattern =>
                    (!isIntegrationSource || PackageSourceOverrideMappings.IsExactPackagePattern(authoritativePattern)) &&
                    PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(pattern, authoritativePattern)));
            if (isIntegrationSource && ambientMappings.Count == 0)
            {
                foreach (var pattern in authoritativePatterns.Where(static pattern =>
                    !PackageSourceOverrideMappings.IsExactPackagePattern(pattern)))
                {
                    AddPattern(patternsBySourceKey, sourceKey, pattern);
                }
            }
        }

        foreach (var mapping in selectedMappings)
        {
            if (ambientMappings.Count > 0 &&
                mapping.PackageFilter == PackageMapping.AllPackages &&
                authoritativeSources.Count > 0 &&
                !authoritativeSources.Contains(mapping.Source))
            {
                // Existing mapping policy already owns unrelated package eligibility. Do not
                // broaden it with a channel fallback source.
                continue;
            }

            foreach (var source in selectedSources.Where(
                source => PackageSourceIdentity.Comparer.Equals(source.Source, mapping.Source)))
            {
                AddPattern(patternsBySourceKey, source.Key, mapping.PackageFilter);
            }
        }

        return patternsBySourceKey
            .Where(static mapping => mapping.Value.Count > 0)
            .Select(static mapping => new NuGetPackageSourceMapping(
                mapping.Key,
                [.. mapping.Value]))
            .ToArray();
    }

    private static NuGetPackageSourceMapping[] ResolveAmbientMappingAliases(NuGetSettingsInfo settings)
    {
        return
        [
            .. settings.PackageSourceMappings.SelectMany<NuGetPackageSourceMapping, NuGetPackageSourceMapping>(mapping =>
            {
                if (settings.Sources.Any(source => string.Equals(source.Name, mapping.SourceKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return [mapping];
                }

                // Older generated configs used a source URL as a mapping key even when the
                // source itself had a different alias. NuGet only matches mapping keys by alias.
                var identity = NuGetSourceIdentity.Compute(mapping.SourceKey, settings.SourceIdentityKey);
                var aliases = settings.Sources.Where(source => string.Equals(source.Identity, identity, StringComparison.Ordinal)).ToArray();
                return aliases.Length == 0
                    ? [mapping]
                    : aliases.Select(source => new NuGetPackageSourceMapping(source.Name, mapping.Patterns)).ToArray();
            })
        ];
    }

    private static void AddPattern(
        Dictionary<string, List<string>> patternsBySourceKey,
        string sourceKey,
        string pattern)
    {
        if (!patternsBySourceKey.TryGetValue(sourceKey, out var patterns))
        {
            patterns = [];
            patternsBySourceKey.Add(sourceKey, patterns);
        }

        if (!patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            patterns.Add(pattern);
        }
    }
}
