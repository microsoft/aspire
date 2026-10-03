// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Provides the effective NuGet configuration for one package or template invocation.
/// </summary>
internal sealed class NuGetPackageSearchConfiguration : IDisposable
{
    private readonly TemporaryNuGetConfig? _temporaryConfig;

    private NuGetPackageSearchConfiguration(
        DirectoryInfo originalWorkingDirectory,
        DirectoryInfo effectiveWorkingDirectory,
        FileInfo? explicitConfigFile,
        FileInfo? configurationFile,
        string cacheIdentity,
        TemporaryNuGetConfig? temporaryConfig)
    {
        OriginalWorkingDirectory = originalWorkingDirectory;
        EffectiveWorkingDirectory = effectiveWorkingDirectory;
        ExplicitConfigFile = explicitConfigFile;
        ConfigurationFile = configurationFile;
        CacheIdentity = cacheIdentity;
        _temporaryConfig = temporaryConfig;
    }

    public DirectoryInfo OriginalWorkingDirectory { get; }

    public DirectoryInfo EffectiveWorkingDirectory { get; }

    /// <summary>
    /// Gets the standalone config passed through an explicit <c>--configfile</c> option.
    /// Ambient overlays remain null so NuGet discovers the overlay and its parent hierarchy.
    /// </summary>
    public FileInfo? ExplicitConfigFile { get; }

    /// <summary>
    /// Gets the generated configuration file, when one exists.
    /// </summary>
    public FileInfo? ConfigurationFile { get; }

    public string CacheIdentity { get; }

    public static NuGetPackageSearchConfiguration Ambient(
        DirectoryInfo workingDirectory,
        string cacheIdentity)
        => new(
            workingDirectory,
            workingDirectory,
            explicitConfigFile: null,
            configurationFile: null,
            cacheIdentity,
            temporaryConfig: null);

    public static NuGetPackageSearchConfiguration Standalone(
        DirectoryInfo workingDirectory,
        TemporaryNuGetConfig temporaryConfig)
        => new(
            workingDirectory,
            workingDirectory,
            temporaryConfig.ConfigFile,
            temporaryConfig.ConfigFile,
            temporaryConfig.CacheIdentity,
            temporaryConfig);

    public static NuGetPackageSearchConfiguration AmbientOverlay(
        DirectoryInfo workingDirectory,
        TemporaryNuGetConfig temporaryConfig,
        string ambientCacheIdentity)
        => new(
            workingDirectory,
            temporaryConfig.ConfigFile.Directory!,
            explicitConfigFile: null,
            temporaryConfig.ConfigFile,
            IntegrationRestorePlanResolver.CombineGlobalPackagesFolderIdentity(
                ambientCacheIdentity,
                temporaryConfig.CacheIdentity),
            temporaryConfig);

    public static NuGetPackageSearchConfiguration Existing(
        DirectoryInfo workingDirectory,
        FileInfo? configurationFile)
        => new(
            workingDirectory,
            workingDirectory,
            configurationFile,
            configurationFile,
            configurationFile?.FullName ?? "ambient",
            temporaryConfig: null);

    public void Dispose()
    {
        _temporaryConfig?.Dispose();
    }
}

/// <summary>
/// Creates invocation-scoped NuGet configurations for package searches and template installation.
/// </summary>
internal sealed class NuGetInvocationConfigurationSource(BundleNuGetService nugetService)
{
    private const string WorkloadId = "package-search";

    public NuGetInvocationConfiguration Resolve(
        DirectoryInfo workingDirectory,
        string workloadId,
        PackageMapping[]? selectedMappings,
        string? packageScopedAppendSource = null,
        bool hasAuthoritativeAspirePolicy = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        var settings = nugetService.GetNuGetSettings(
            workingDirectory.FullName,
            cancellationToken);
        var configSources = NuGetInvocationConfigurationComposer.ResolveConfigSources(
            selectedMappings,
            workloadId,
            settings.Sources,
            settings.ReservedPackageSourceKeys,
            settings.SourceIdentityKey);

        return new NuGetInvocationConfiguration(
            selectedMappings,
            settings,
            configSources,
            packageScopedAppendSource,
            hasAuthoritativeAspirePolicy,
            nugetService);
    }

    public async Task<NuGetPackageSearchConfiguration> CreateAmbientOverlayAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? channelMappings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var selectedMappings = channelMappings?
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .ToArray() ?? [];
        if (selectedMappings.Length == 0)
        {
            var ambientConfiguration = Resolve(
                workingDirectory,
                WorkloadId,
                selectedMappings: null,
                cancellationToken: cancellationToken);
            return NuGetPackageSearchConfiguration.Ambient(
                workingDirectory,
                ambientConfiguration.Settings.CacheIdentity);
        }

        var invocationConfiguration = Resolve(
            workingDirectory,
            WorkloadId,
            selectedMappings,
            hasAuthoritativeAspirePolicy: true,
            cancellationToken: cancellationToken);
        var temporaryConfig = await invocationConfiguration.CreateTemporaryOverlayAsync(
            workingDirectory,
            globalPackagesFolder: null,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected package mappings did not produce a NuGet policy overlay.");

        return NuGetPackageSearchConfiguration.AmbientOverlay(
            workingDirectory,
            temporaryConfig,
            invocationConfiguration.Settings.CacheIdentity);
    }

    public static async Task<NuGetPackageSearchConfiguration> CreateStandaloneAsync(
        DirectoryInfo workingDirectory,
        PackageMapping[] mappings)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(mappings);

        var temporaryConfig = await TemporaryNuGetConfig.CreateAsync(mappings).ConfigureAwait(false);
        return NuGetPackageSearchConfiguration.Standalone(
            workingDirectory,
            temporaryConfig);
    }
}

/// <summary>
/// Represents ambient NuGet settings combined with one invocation's selected source policy.
/// </summary>
internal sealed class NuGetInvocationConfiguration
{
    private readonly string? _packageScopedAppendSource;
    private readonly bool _hasAuthoritativeAspirePolicy;
    private readonly BundleNuGetService _nugetService;

    public NuGetInvocationConfiguration(
        PackageMapping[]? selectedMappings,
        NuGetSettingsInfo settings,
        NuGetConfigSource[] configSources,
        string? packageScopedAppendSource,
        bool hasAuthoritativeAspirePolicy,
        BundleNuGetService nugetService)
    {
        SelectedMappings = selectedMappings is null ? null : [.. selectedMappings];
        Settings = settings with
        {
            ConfigPaths = [.. settings.ConfigPaths],
            Sources = [.. settings.Sources],
            SensitiveSourceValues = [.. settings.SensitiveSourceValues],
            PackageSourceMappings = [.. settings.PackageSourceMappings],
            DisabledPackageSourceKeys = [.. settings.DisabledPackageSourceKeys],
            ReservedPackageSourceKeys = [.. settings.ReservedPackageSourceKeys],
            SourceIdentityKey = [.. settings.SourceIdentityKey]
        };
        ConfigSources = [.. configSources];
        _packageScopedAppendSource = packageScopedAppendSource;
        _hasAuthoritativeAspirePolicy = hasAuthoritativeAspirePolicy;
        _nugetService = nugetService;
    }

    public PackageMapping[]? SelectedMappings { get; }

    public NuGetSettingsInfo Settings { get; }

    public IReadOnlyList<NuGetConfigSource> ConfigSources { get; }

    public NuGetConfigOverlay CreateOverlay(string? globalPackagesFolder)
    {
        if (SelectedMappings is null)
        {
            throw new InvalidOperationException("An invocation without selected mappings does not require a NuGet policy overlay.");
        }

        return NuGetInvocationConfigurationComposer.CreateOverlay(
            SelectedMappings,
            Settings,
            ConfigSources,
            globalPackagesFolder,
            _packageScopedAppendSource,
            _hasAuthoritativeAspirePolicy);
    }

    public async Task<TemporaryNuGetConfig?> CreateTemporaryOverlayAsync(
        DirectoryInfo? parentDirectory,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        if (SelectedMappings is null)
        {
            return null;
        }

        var overlay = CreateOverlay(globalPackagesFolder);
        return parentDirectory is null
            ? await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
                path => _nugetService.WriteNuGetConfigOverlay(
                    overlay,
                    path,
                    cancellationToken)).ConfigureAwait(false)
            : await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
                parentDirectory,
                path => _nugetService.WriteNuGetConfigOverlay(
                    overlay,
                    path,
                    cancellationToken)).ConfigureAwait(false);
    }

    public void WriteOverlay(
        string outputPath,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        _nugetService.WriteNuGetConfigOverlay(
            CreateOverlay(globalPackagesFolder),
            outputPath,
            cancellationToken);
    }
}

internal static class NuGetInvocationConfigurationComposer
{
    public static NuGetConfigSource[] ResolveConfigSources(
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
            var ambientMatches = ambientSources
                .Where(candidate => string.Equals(candidate.Identity, sourceIdentity, StringComparison.Ordinal))
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
                        ambientSource.IsEnabled));
                }

                continue;
            }

            string key;
            if (isPrimarySource)
            {
                key = generatedSourceKeyPrefix;
                if (!usedKeys.Add(key))
                {
                    throw new InvalidOperationException(
                        $"The generated NuGet source key '{key}' conflicts with an existing NuGet configuration key.");
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

            resolvedSources.Add(new NuGetConfigSource(key, source, IsAmbient: false, IsEnabled: true));
        }

        return [.. resolvedSources];
    }

    public static NuGetConfigOverlay CreateOverlay(
        PackageMapping[] selectedMappings,
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> selectedSources,
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

        return new NuGetConfigOverlay(
            selectedSources
                .Where(static source => !source.IsAmbient)
                .Select(static source => (Key: source.Key, Source: source.Source))
                .ToArray(),
            ComposePackageSourceMappings(
                selectedMappings,
                settings.PackageSourceMappings,
                settings.Sources,
                selectedSources,
                packageScopedAppendSource,
                hasAuthoritativeAspirePolicy),
            clearDisabledPackageSources,
            disabledPackageSourceKeys,
            globalPackagesFolder);
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
        foreach (var patterns in patternsBySourceKey.Values)
        {
            patterns.RemoveAll(pattern => authoritativePatterns.Any(
                authoritativePattern => PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(
                    pattern,
                    authoritativePattern)));
        }

        foreach (var mapping in selectedMappings)
        {
            if (ambientMappings.Count > 0 &&
                mapping.PackageFilter == PackageMapping.AllPackages &&
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
