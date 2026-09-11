// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Represents one resolved source-policy snapshot shared by the polyglot integration restore paths.
/// </summary>
internal interface IIntegrationRestorePlan
{
    string? EffectivePackageSourceOverride { get; }

    string GetRestoreVersion(string packageName, string version);

    Task<IntegrationPackageRestoreConfiguration> CreatePackageRestoreConfigurationAsync(
        CancellationToken cancellationToken);

    Task<IntegrationProjectRestoreConfiguration> ApplyProjectRestoreConfigurationAsync(
        DirectoryInfo policyDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves channel and ambient NuGet state once for one AppHost preparation.
/// </summary>
internal sealed class IntegrationRestorePlanResolver(
    IPackagingService packagingService,
    BundleNuGetService nugetService,
    CliExecutionContext executionContext,
    ILogger logger)
{
    public async Task<IIntegrationRestorePlan> ResolveAsync(
        string appDirectoryPath,
        string sdkVersion,
        string? requestedChannel,
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkVersion);

        ThrowIfStagingUnavailable(requestedChannel);

        var effectivePackageSourceOverride = packageSourceOverride;
        var localSourceDiscoveryChannel = requestedChannel;
        if (string.IsNullOrWhiteSpace(effectivePackageSourceOverride) &&
            localSourceDiscoveryChannel is null &&
            string.Equals(sdkVersion, executionContext.IdentitySdkVersion, StringComparison.OrdinalIgnoreCase))
        {
            // The identity channel is used only to discover the co-installed local source. It must
            // not become the project's channel policy when the project did not request a channel.
            localSourceDiscoveryChannel = executionContext.IdentityChannel;
        }

        var channelLookupName = requestedChannel ?? localSourceDiscoveryChannel;
        PackageChannel? requestedPolicyChannel = null;
        PackageChannel? sourceDiscoveryChannel = null;
        var channelLookupSucceeded = false;

        if (!string.IsNullOrEmpty(channelLookupName))
        {
            try
            {
                var channels = (await packagingService.GetChannelsAsync(
                    cancellationToken,
                    channelLookupName).ConfigureAwait(false)).ToArray();
                channelLookupSucceeded = true;
                requestedPolicyChannel = FindChannel(channels, requestedChannel);
                sourceDiscoveryChannel = FindChannel(channels, localSourceDiscoveryChannel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                !string.IsNullOrWhiteSpace(packageSourceOverride) ||
                string.IsNullOrEmpty(requestedChannel))
            {
                logger.LogWarning(
                    ex,
                    "Failed to resolve integration restore package channels, relying on configured NuGet sources.");
            }
        }

        if (channelLookupSucceeded &&
            !string.IsNullOrEmpty(requestedChannel) &&
            requestedPolicyChannel is null)
        {
            throw new InvalidOperationException($"Package channel '{requestedChannel}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(effectivePackageSourceOverride))
        {
            effectivePackageSourceOverride = sourceDiscoveryChannel?.GetExistingLocalAspirePackageSource();
            if (!string.IsNullOrWhiteSpace(effectivePackageSourceOverride))
            {
                logger.LogDebug(
                    "Using local package source '{Source}' for channel '{Channel}'.",
                    effectivePackageSourceOverride,
                    localSourceDiscoveryChannel);
            }
        }

        var restoreSources = ResolveSources(
            effectivePackageSourceOverride,
            packageSourceOverridePattern,
            requestedPolicyChannel,
            executionContext.NuGetServiceIndexOverride);
        restoreSources = NormalizeSources(restoreSources, new DirectoryInfo(appDirectoryPath));

        var settings = await nugetService.GetNuGetSettingsAsync(
            appDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        var configSources = IntegrationRestorePlan.ResolveNuGetConfigSources(
            restoreSources.PackageSourceMappings,
            settings.Sources,
            settings.ReservedPackageSourceKeys,
            settings.SourceIdentityKey);

        return new IntegrationRestorePlan(
            effectivePackageSourceOverride,
            restoreSources,
            settings,
            configSources,
            appDirectoryPath,
            executionContext.AspireHomeDirectory,
            nugetService);
    }

    private void ThrowIfStagingUnavailable(string? requestedChannel)
    {
        if (!string.Equals(requestedChannel, PackageChannelNames.Staging, StringComparisons.ChannelName))
        {
            return;
        }

        var reason = packagingService.GetStagingChannelUnavailableReason();
        if (reason is not null)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static PackageChannel? FindChannel(
        IReadOnlyList<PackageChannel> channels,
        string? channelName)
        => string.IsNullOrEmpty(channelName)
            ? null
            : channels.FirstOrDefault(channel =>
                string.Equals(channel.Name, channelName, StringComparisons.ChannelName));

    private static IntegrationRestoreSources ResolveSources(
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        PackageChannel? requestedPolicyChannel,
        string? nugetServiceIndexOverride)
    {
        var additionalSources = new List<string>();
        var hasOverride = !string.IsNullOrWhiteSpace(packageSourceOverride);
        if (hasOverride)
        {
            additionalSources.Add(packageSourceOverride!);
        }

        var sourcePolicyChannel = requestedPolicyChannel is not null && !UsesAmbientSourcePolicy(requestedPolicyChannel)
            ? requestedPolicyChannel
            : null;
        if (sourcePolicyChannel?.Mappings is { } channelMappings)
        {
            foreach (var mapping in channelMappings)
            {
                if (hasOverride && IsAspireSpecificMapping(mapping))
                {
                    continue;
                }

                if (!additionalSources.Contains(mapping.Source, PackageSourceIdentity.Comparer))
                {
                    additionalSources.Add(mapping.Source);
                }
            }
        }

        PackageMapping[]? packageSourceMappings = null;
        var configureGlobalPackagesFolder = false;
        if (hasOverride)
        {
            packageSourceMappings = PackageSourceOverrideMappings.Create(
                packageSourceOverride!,
                sourcePolicyChannel,
                nugetServiceIndexOverride,
                packageSourceOverridePattern);
            configureGlobalPackagesFolder = sourcePolicyChannel?.ConfigureGlobalPackagesFolder == true;

            foreach (var mapping in packageSourceMappings.Where(
                static mapping => mapping.PackageFilter == PackageMapping.AllPackages))
            {
                if (!additionalSources.Contains(mapping.Source, PackageSourceIdentity.Comparer))
                {
                    additionalSources.Add(mapping.Source);
                }
            }
        }
        else if (sourcePolicyChannel?.Mappings is { Length: > 0 } &&
            !string.Equals(sourcePolicyChannel.Name, PackageChannelNames.Local, StringComparisons.ChannelName))
        {
            packageSourceMappings = [.. sourcePolicyChannel.Mappings];
            configureGlobalPackagesFolder = sourcePolicyChannel.ConfigureGlobalPackagesFolder;
        }

        return new IntegrationRestoreSources(
            [.. additionalSources],
            packageSourceMappings,
            configureGlobalPackagesFolder,
            configureGlobalPackagesFolder
                ? CreateGlobalPackagesFolderIdentity(additionalSources, packageSourceMappings)
                : null);
    }

    private static IntegrationRestoreSources NormalizeSources(
        IntegrationRestoreSources restoreSources,
        DirectoryInfo appDirectory)
    {
        var normalizedAdditionalSources = restoreSources.AdditionalSources
            .Select(source => PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, appDirectory))
            .ToArray();
        var normalizedMappings = restoreSources.PackageSourceMappings?
            .Select(mapping => new PackageMapping(
                mapping.PackageFilter,
                PackageSourceOverrideMappings.ResolveForWorkingDirectory(mapping.Source, appDirectory)))
            .ToArray();

        return restoreSources with
        {
            AdditionalSources = normalizedAdditionalSources,
            PackageSourceMappings = normalizedMappings,
            GlobalPackagesFolderIdentity = restoreSources.ConfigureGlobalPackagesFolder
                ? CreateGlobalPackagesFolderIdentity(normalizedAdditionalSources, normalizedMappings)
                : null
        };
    }

    internal static string CreateGlobalPackagesFolderIdentity(
        IReadOnlyList<string> additionalSources,
        IReadOnlyList<PackageMapping>? mappings)
    {
        var builder = new StringBuilder();
        IEnumerable<PackageMapping> orderedMappings = mappings is null
            ? []
            : mappings
                .OrderBy(static mapping => mapping.PackageFilter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static mapping => PackageSourceIdentity.Normalize(mapping.Source), StringComparer.Ordinal);
        foreach (var mapping in orderedMappings)
        {
            AppendIdentityPart(builder, mapping.PackageFilter.ToUpperInvariant());
            AppendIdentityPart(builder, PackageSourceIdentity.Normalize(mapping.Source));
        }

        foreach (var source in additionalSources
            .Distinct(PackageSourceIdentity.Comparer)
            .OrderBy(PackageSourceIdentity.Normalize, StringComparer.Ordinal))
        {
            AppendIdentityPart(builder, PackageSourceIdentity.Normalize(source));
        }

        return builder.ToString();
    }

    private static void AppendIdentityPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
    }

    private static bool IsAspireSpecificMapping(PackageMapping mapping) =>
        mapping.PackageFilter != PackageMapping.AllPackages &&
        mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);

    private static bool UsesAmbientSourcePolicy(PackageChannel channel) =>
        string.Equals(channel.Name, PackageChannelNames.Stable, StringComparisons.ChannelName);
}

/// <summary>
/// Materializes restore-specific configuration from one resolved channel and NuGet settings snapshot.
/// </summary>
internal sealed class IntegrationRestorePlan : IIntegrationRestorePlan
{
    private readonly IntegrationRestoreSources _restoreSources;
    private readonly NuGetSettingsInfo _settings;
    private readonly NuGetConfigSource[] _configSources;
    private readonly string _appDirectoryPath;
    private readonly DirectoryInfo _aspireHomeDirectory;
    private readonly BundleNuGetService _nugetService;

    public IntegrationRestorePlan(
        string? effectivePackageSourceOverride,
        IntegrationRestoreSources restoreSources,
        NuGetSettingsInfo settings,
        NuGetConfigSource[] configSources,
        string appDirectoryPath,
        DirectoryInfo aspireHomeDirectory,
        BundleNuGetService nugetService)
    {
        EffectivePackageSourceOverride = effectivePackageSourceOverride;
        _restoreSources = restoreSources with
        {
            AdditionalSources = [.. restoreSources.AdditionalSources],
            PackageSourceMappings = restoreSources.PackageSourceMappings is null
                ? null
                : [.. restoreSources.PackageSourceMappings]
        };
        _settings = settings with
        {
            ConfigPaths = [.. settings.ConfigPaths],
            Sources = [.. settings.Sources],
            SensitiveSourceValues = [.. settings.SensitiveSourceValues],
            PackageSourceMappings = [.. settings.PackageSourceMappings],
            DisabledPackageSourceKeys = [.. settings.DisabledPackageSourceKeys],
            ReservedPackageSourceKeys = [.. settings.ReservedPackageSourceKeys],
            SourceIdentityKey = [.. settings.SourceIdentityKey]
        };
        _configSources = [.. configSources];
        _appDirectoryPath = appDirectoryPath;
        _aspireHomeDirectory = aspireHomeDirectory;
        _nugetService = nugetService;
    }

    public string? EffectivePackageSourceOverride { get; }

    internal IReadOnlyList<string> AdditionalSources => _restoreSources.AdditionalSources;

    public string GetRestoreVersion(string packageName, string version)
    {
        var useExactAspirePackageVersion =
            !string.IsNullOrWhiteSpace(EffectivePackageSourceOverride) &&
            packageName.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);
        if (!useExactAspirePackageVersion || version.Length == 0 || version[0] is '[' or '(')
        {
            return version;
        }

        return $"[{version}]";
    }

    public async Task<IntegrationPackageRestoreConfiguration> CreatePackageRestoreConfigurationAsync(
        CancellationToken cancellationToken)
    {
        var overlay = await CreateRestoreOverlayAsync(cancellationToken).ConfigureAwait(false);
        var sources = GetNuGetSources()?.ToArray();
        string[] configPaths = overlay is null
            ? [.. _settings.ConfigPaths]
            : [overlay.ConfigFile.FullName, .. _settings.ConfigPaths];
        var sensitiveSources = _settings.SensitiveSourceValues
            .Concat(
                _restoreSources.PackageSourceMappings?
                    .Select(static mapping => mapping.Source)
                    .Where(PackageSourceOverrideMappings.HasCredentialMaterial) ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new IntegrationPackageRestoreConfiguration(
            overlay,
            sources,
            configPaths,
            _settings.CacheIdentity,
            sensitiveSources,
            GetGlobalPackagesFolder(overlay));
    }

    public async Task<IntegrationProjectRestoreConfiguration> ApplyProjectRestoreConfigurationAsync(
        DirectoryInfo policyDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policyDirectory);

        var selectedSensitiveSources = _restoreSources.AdditionalSources
            .Concat(_restoreSources.PackageSourceMappings?.Select(static mapping => mapping.Source) ?? [])
            .Where(PackageSourceOverrideMappings.HasCredentialMaterial)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var globalPackagesFolder = GetGlobalPackagesFolder(restoreOverlay: null);
        FileInfo? restoreOverlayFile = new(Path.Combine(policyDirectory.FullName, "NuGet.Config"));
        if (restoreOverlayFile.Exists)
        {
            restoreOverlayFile.Delete();
        }

        if (_restoreSources.PackageSourceMappings is null)
        {
            restoreOverlayFile = null;
        }
        else
        {
            var overlay = CreateNuGetConfigOverlay(
                _restoreSources.PackageSourceMappings,
                _settings,
                _configSources,
                globalPackagesFolder);
            await _nugetService.WriteNuGetConfigOverlayAsync(
                overlay,
                restoreOverlayFile.FullName,
                cancellationToken).ConfigureAwait(false);
        }

        var rootAdditionalSources = _restoreSources.PackageSourceMappings is null
            ? GetNuGetSources()?.ToArray() ?? []
            : [];
        var sensitiveSources = _settings.SensitiveSourceValues
            .Concat(selectedSensitiveSources)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new IntegrationProjectRestoreConfiguration(
            restoreOverlayFile?.DirectoryName ?? _appDirectoryPath,
            rootAdditionalSources,
            GetIntegrationPackageSourceHints(),
            sensitiveSources,
            globalPackagesFolder);
    }

    internal static NuGetConfigSource[] ResolveNuGetConfigSources(
        PackageMapping[]? mappings,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<string> reservedPackageSourceKeys,
        ReadOnlySpan<byte> sourceIdentityKey)
    {
        if (mappings is null)
        {
            return [];
        }

        var usedKeys = reservedPackageSourceKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextAspireKey = 0;

        var resolvedSources = new List<NuGetConfigSource>();
        foreach (var source in mappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer))
        {
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
            do
            {
                key = $"aspire-{nextAspireKey++}";
            }
            while (!usedKeys.Add(key));

            resolvedSources.Add(new NuGetConfigSource(key, source, IsAmbient: false, IsEnabled: true));
        }

        return [.. resolvedSources];
    }

    internal static NuGetConfigOverlayRequest CreateNuGetConfigOverlay(
        PackageMapping[] selectedMappings,
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        string? globalPackagesFolder)
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

        return new NuGetConfigOverlayRequest(
            selectedSources
                .Where(static source => !source.IsAmbient)
                .Select(static source => new NuGetConfigSourceDefinition(source.Key, source.Source))
                .ToArray(),
            ComposePackageSourceMappings(
                selectedMappings,
                settings.PackageSourceMappings,
                settings.Sources,
                selectedSources),
            clearDisabledPackageSources,
            disabledPackageSourceKeys,
            globalPackagesFolder);
    }

    internal static NuGetPackageSourceMapping[] ComposePackageSourceMappings(
        IReadOnlyList<PackageMapping> selectedMappings,
        IReadOnlyList<NuGetPackageSourceMapping> ambientMappings,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<NuGetConfigSource> selectedSources)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(ambientMappings);
        ArgumentNullException.ThrowIfNull(ambientSources);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var patternsBySourceKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

        var authoritativePatterns = selectedMappings
            .Select(static mapping => mapping.PackageFilter)
            .Where(static pattern => pattern != PackageMapping.AllPackages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var authoritativeSources = selectedMappings
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .Select(static mapping => mapping.Source)
            .ToHashSet(PackageSourceIdentity.Comparer);
        foreach (var patterns in patternsBySourceKey.Values)
        {
            patterns.RemoveAll(pattern => authoritativePatterns.Any(
                authoritativePattern => CompetesWithAuthoritativePattern(pattern, authoritativePattern)));
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

    private IEnumerable<string>? GetNuGetSources()
        => _restoreSources.PackageSourceMappings is null && _restoreSources.AdditionalSources.Count > 0
            ? _restoreSources.AdditionalSources
            : null;

    private string[] GetIntegrationPackageSourceHints()
    {
        IEnumerable<string> candidateSources;
        if (_restoreSources.PackageSourceMappings is { Length: > 0 } mappings)
        {
            var integrationSpecificSources = mappings
                .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
                .Select(static mapping => mapping.Source)
                .ToArray();
            candidateSources = integrationSpecificSources.Length > 0
                ? integrationSpecificSources
                : mappings
                    .Where(static mapping => mapping.PackageFilter == PackageMapping.AllPackages)
                    .Select(static mapping => mapping.Source);
        }
        else
        {
            candidateSources = _restoreSources.AdditionalSources;
        }

        // Source hints flow through MSBuild and can be persisted in restore artifacts. Credential
        // bearing URLs must use NuGet-owned authentication instead of being copied into MSBuild.
        return candidateSources
            .Where(static source => !PackageSourceOverrideMappings.HasCredentialMaterial(source))
            .Distinct(PackageSourceIdentity.Comparer)
            .ToArray();
    }

    private string? GetGlobalPackagesFolder(TemporaryNuGetConfig? restoreOverlay)
        => _restoreSources.ConfigureGlobalPackagesFolder
            ? CliPathHelper.GetStagingNuGetPackagesIdentityDirectory(
                _aspireHomeDirectory,
                restoreOverlay?.CacheIdentity ?? _restoreSources.GlobalPackagesFolderIdentity)
            : null;

    private async Task<TemporaryNuGetConfig?> CreateRestoreOverlayAsync(
        CancellationToken cancellationToken)
    {
        if (_restoreSources.PackageSourceMappings is null)
        {
            return null;
        }

        var overlay = CreateNuGetConfigOverlay(
            _restoreSources.PackageSourceMappings,
            _settings,
            _configSources,
            globalPackagesFolder: null);
        var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => _nugetService.WriteNuGetConfigOverlayAsync(
                overlay,
                path,
                cancellationToken)).ConfigureAwait(false);
        return await ConfigureGlobalPackagesFolderAsync(
            config,
            overlay,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TemporaryNuGetConfig> ConfigureGlobalPackagesFolderAsync(
        TemporaryNuGetConfig config,
        NuGetConfigOverlayRequest overlay,
        CancellationToken cancellationToken)
    {
        var globalPackagesFolder = GetGlobalPackagesFolder(config);
        if (globalPackagesFolder is null)
        {
            return config;
        }

        try
        {
            await config.RegenerateAsync(
                path => _nugetService.WriteNuGetConfigOverlayAsync(
                    overlay with { GlobalPackagesFolder = globalPackagesFolder },
                    path,
                    cancellationToken)).ConfigureAwait(false);
            return config;
        }
        catch
        {
            config.Dispose();
            throw;
        }
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

    private static bool CompetesWithAuthoritativePattern(
        string ambientPattern,
        string authoritativePattern)
    {
        if (authoritativePattern == PackageMapping.AllPackages)
        {
            return false;
        }

        if (!authoritativePattern.EndsWith('*'))
        {
            return string.Equals(
                ambientPattern,
                authoritativePattern,
                StringComparison.OrdinalIgnoreCase);
        }

        var authoritativePrefix = authoritativePattern[..^1];
        if (ambientPattern == PackageMapping.AllPackages)
        {
            return false;
        }

        var ambientPrefix = ambientPattern.EndsWith('*')
            ? ambientPattern[..^1]
            : ambientPattern;
        return ambientPrefix.Length >= authoritativePrefix.Length &&
            ambientPrefix.StartsWith(authoritativePrefix, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record NuGetConfigSource(
    string Key,
    string Source,
    bool IsAmbient,
    bool IsEnabled);

internal sealed record IntegrationRestoreSources(
    IReadOnlyList<string> AdditionalSources,
    PackageMapping[]? PackageSourceMappings,
    bool ConfigureGlobalPackagesFolder,
    string? GlobalPackagesFolderIdentity);

internal sealed class IntegrationPackageRestoreConfiguration(
    TemporaryNuGetConfig? policyOverlay,
    string[]? sources,
    string[] configPaths,
    string settingsCacheIdentity,
    string[] sensitiveSources,
    string? globalPackagesFolder) : IDisposable
{
    public string[]? Sources { get; } = sources;

    public string[] ConfigPaths { get; } = configPaths;

    public string SettingsCacheIdentity { get; } = settingsCacheIdentity;

    public string? OverlayCacheIdentity => policyOverlay?.CacheIdentity;

    public string[] SensitiveSources { get; } = sensitiveSources;

    public string? GlobalPackagesFolder { get; } = globalPackagesFolder;

    internal FileInfo? PolicyOverlayFile => policyOverlay?.ConfigFile;

    public void Dispose() => policyOverlay?.Dispose();
}

internal sealed record IntegrationProjectRestoreConfiguration(
    string RestoreRootConfigDirectory,
    string[] RootAdditionalSources,
    string[] PackageSourceHints,
    string[] SensitiveSources,
    string? GlobalPackagesFolder);
