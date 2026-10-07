// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Resolves channel and ambient NuGet state once for one AppHost preparation.
/// </summary>
internal sealed class IntegrationRestorePlanResolver(
    IPackagingService packagingService,
    BundleNuGetService nugetService,
    CliExecutionContext executionContext,
    ILogger logger)
{
    public async Task<IntegrationRestorePlan> ResolveAsync(
        string appDirectoryPath,
        string workloadId,
        string sdkVersion,
        string? requestedChannel,
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkVersion);

        ThrowIfStagingUnavailable(requestedChannel);

        var effectivePackageSourceOverride = packageSourceOverride;
        var explicitPackageSourceOverridePattern = string.IsNullOrWhiteSpace(packageSourceOverride)
            ? null
            : PackageSourceOverrideMappings.GetEffectivePackagePattern(packageSourceOverridePattern);
        var localSourceDiscoveryChannel = requestedChannel;
        var canAppendIdentitySource =
            explicitPackageSourceOverridePattern is not null &&
            PackageSourceOverrideMappings.IsExactPackagePattern(explicitPackageSourceOverridePattern);
        if ((string.IsNullOrWhiteSpace(effectivePackageSourceOverride) || canAppendIdentitySource) &&
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

        var effectivePackageSourceOverridePattern = string.IsNullOrWhiteSpace(effectivePackageSourceOverride)
            ? null
            : explicitPackageSourceOverridePattern ?? PackageSourceOverrideMappings.DefaultPackagePattern;
        var restoreSources = ResolveSources(
            effectivePackageSourceOverride,
            effectivePackageSourceOverridePattern,
            requestedPolicyChannel,
            sourceDiscoveryChannel,
            executionContext.NuGetServiceIndexOverride);
        restoreSources = NormalizeSources(restoreSources, new DirectoryInfo(appDirectoryPath));

        var configuration = nugetService.BuildConfiguration(
            new DirectoryInfo(appDirectoryPath),
            workloadId,
            restoreSources.PackageSourceMappings,
            restrictToSelectedSources: false,
            restoreSources.PackageScopedAppendSource,
            restoreSources.HasAuthoritativeAspirePolicy,
            cancellationToken);

        return new IntegrationRestorePlan(
            effectivePackageSourceOverride,
            effectivePackageSourceOverridePattern,
            restoreSources,
            configuration,
            nugetService,
            appDirectoryPath,
            executionContext.AspireHomeDirectory);
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
        PackageChannel? sourceDiscoveryChannel,
        string? nugetServiceIndexOverride)
    {
        var additionalSources = new List<string>();
        var hasOverride = !string.IsNullOrWhiteSpace(packageSourceOverride);
        if (hasOverride)
        {
            additionalSources.Add(packageSourceOverride!);
        }

        var selectedSourcePolicyChannel = requestedPolicyChannel;
        if (selectedSourcePolicyChannel is null &&
            hasOverride &&
            packageSourceOverridePattern is not null &&
            PackageSourceOverrideMappings.IsExactPackagePattern(packageSourceOverridePattern))
        {
            selectedSourcePolicyChannel = sourceDiscoveryChannel;
        }

        var sourcePolicyChannel = selectedSourcePolicyChannel is not null && !UsesAmbientSourcePolicy(selectedSourcePolicyChannel)
            ? selectedSourcePolicyChannel
            : null;
        var packageScopedAppendSource =
            hasOverride &&
            packageSourceOverridePattern is not null &&
            PackageSourceOverrideMappings.IsExactPackagePattern(packageSourceOverridePattern)
                ? packageSourceOverride
                : null;
        var hasAuthoritativeAspirePolicy = sourcePolicyChannel?.Mappings?.Any(mapping =>
            (!hasOverride ||
             packageSourceOverridePattern is null ||
             !PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(
                 mapping.PackageFilter,
                 packageSourceOverridePattern)) &&
            IsAspireSpecificMapping(mapping)) == true;
        if (sourcePolicyChannel?.Mappings is { } channelMappings)
        {
            foreach (var mapping in channelMappings)
            {
                if (mapping.PackageFilter == PackageMapping.AllPackages)
                {
                    // Channel catch-all mappings supported the former standalone configuration.
                    // The integration restore now preserves ambient sources and mappings instead.
                    continue;
                }

                if (hasOverride &&
                    packageSourceOverridePattern is not null &&
                    PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(
                        mapping.PackageFilter,
                        packageSourceOverridePattern))
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
                packageSourceOverridePattern)
                .Where(mapping =>
                    mapping.PackageFilter != PackageMapping.AllPackages ||
                    PackageSourceIdentity.Comparer.Equals(mapping.Source, packageSourceOverride))
                .ToArray();
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
            packageSourceMappings =
            [
                .. sourcePolicyChannel.Mappings.Where(
                    static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            ];
            configureGlobalPackagesFolder = sourcePolicyChannel.ConfigureGlobalPackagesFolder;
        }
        return new IntegrationRestoreSources(
            [.. additionalSources],
            packageSourceMappings,
            packageScopedAppendSource,
            hasAuthoritativeAspirePolicy,
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
            PackageScopedAppendSource = restoreSources.PackageScopedAppendSource is null
                ? null
                : PackageSourceOverrideMappings.ResolveForWorkingDirectory(
                    restoreSources.PackageScopedAppendSource,
                    appDirectory),
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
internal sealed class IntegrationRestorePlan
{
    private readonly string? _effectivePackageSourceOverridePattern;
    private readonly IntegrationRestoreSources _restoreSources;
    private readonly NuGetConfiguration _configuration;
    private readonly BundleNuGetService _nugetService;
    private readonly string _appDirectoryPath;
    private readonly DirectoryInfo _aspireHomeDirectory;

    public IntegrationRestorePlan(
        string? effectivePackageSourceOverride,
        string? effectivePackageSourceOverridePattern,
        IntegrationRestoreSources restoreSources,
        NuGetConfiguration configuration,
        BundleNuGetService nugetService,
        string appDirectoryPath,
        DirectoryInfo aspireHomeDirectory)
    {
        EffectivePackageSourceOverride = effectivePackageSourceOverride;
        _effectivePackageSourceOverridePattern = effectivePackageSourceOverridePattern;
        _restoreSources = restoreSources with
        {
            AdditionalSources = [.. restoreSources.AdditionalSources],
            PackageSourceMappings = restoreSources.PackageSourceMappings is null
                ? null
                : [.. restoreSources.PackageSourceMappings]
        };
        _configuration = configuration;
        _nugetService = nugetService;
        _appDirectoryPath = appDirectoryPath;
        _aspireHomeDirectory = aspireHomeDirectory;
    }

    public string? EffectivePackageSourceOverride { get; }

    internal IReadOnlyList<string> AdditionalSources => _restoreSources.AdditionalSources;

    public string GetRestoreVersion(string packageName, string version)
    {
        var useExactPackageVersion =
            _effectivePackageSourceOverridePattern is not null &&
            PackageSourceOverrideMappings.MatchesPackage(
                _effectivePackageSourceOverridePattern,
                packageName);
        if (!useExactPackageVersion || version.Length == 0 || version[0] is '[' or '(')
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
            ? [.. _configuration.Settings.ConfigPaths]
            : [overlay.ConfigFile.FullName, .. _configuration.Settings.ConfigPaths];
        var sensitiveSources = _configuration.Settings.SensitiveSourceValues
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
            _configuration.Settings.CacheIdentity,
            sensitiveSources,
            GetGlobalPackagesFolder(overlay));
    }

    public IntegrationProjectRestoreConfiguration ApplyProjectRestoreConfiguration(
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
            _nugetService.WriteNuGetConfig(
                _configuration,
                restoreOverlayFile.FullName,
                globalPackagesFolder,
                cancellationToken);
        }

        var rootAdditionalSources = _restoreSources.PackageSourceMappings is null
            ? GetNuGetSources()?.ToArray() ?? []
            : [];
        var sensitiveSources = _configuration.Settings.SensitiveSourceValues
            .Concat(selectedSensitiveSources)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var packageSourceHints = GetIntegrationPackageSourceHints();

        return new IntegrationProjectRestoreConfiguration(
            restoreOverlayFile?.DirectoryName ?? _appDirectoryPath,
            rootAdditionalSources,
            packageSourceHints,
            GetIntegrationPackageSourceAlias(packageSourceHints),
            sensitiveSources,
            globalPackagesFolder);
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

    private string? GetIntegrationPackageSourceAlias(IReadOnlyList<string> packageSourceHints)
    {
        if (packageSourceHints.Count == 0)
        {
            return null;
        }

        var primarySource = packageSourceHints[0];
        return _configuration.ConfigSources
            .FirstOrDefault(source => PackageSourceIdentity.Comparer.Equals(source.Source, primarySource))
            ?.Key ?? primarySource;
    }

    private string? GetGlobalPackagesFolder(TemporaryNuGetConfigFile? restoreOverlay)
    {
        if (!_restoreSources.ConfigureGlobalPackagesFolder)
        {
            return null;
        }

        var identity = _restoreSources.GlobalPackagesFolderIdentity
            ?? throw new InvalidOperationException("A global packages folder identity is required for an isolated restore cache.");
        if (restoreOverlay is not null)
        {
            // Reused ambient aliases are not re-emitted with their source URLs, so the overlay
            // identity must be paired with the selected source policy to distinguish those feeds.
            identity = BundleNuGetService.CombineCacheIdentities(
                identity,
                restoreOverlay.CacheIdentity);
        }

        return CliPathHelper.GetStagingNuGetPackagesIdentityDirectory(
            _aspireHomeDirectory,
            identity);
    }

    private async Task<TemporaryNuGetConfigFile?> CreateRestoreOverlayAsync(
        CancellationToken cancellationToken)
    {
        if (_restoreSources.PackageSourceMappings is null)
        {
            return null;
        }

        var config = await _nugetService.WriteTemporaryOverlayAsync(
            _configuration,
            parentDirectory: null,
            globalPackagesFolder: null,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Integration restore mappings did not produce a NuGet policy overlay.");
        return await ConfigureGlobalPackagesFolderAsync(
            config,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TemporaryNuGetConfigFile> ConfigureGlobalPackagesFolderAsync(
        TemporaryNuGetConfigFile config,
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
                path => _nugetService.WriteNuGetConfig(
                    _configuration,
                    path,
                    globalPackagesFolder,
                    cancellationToken)).ConfigureAwait(false);
            return config;
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }

}

internal sealed record IntegrationRestoreSources(
    IReadOnlyList<string> AdditionalSources,
    PackageMapping[]? PackageSourceMappings,
    string? PackageScopedAppendSource,
    bool HasAuthoritativeAspirePolicy,
    bool ConfigureGlobalPackagesFolder,
    string? GlobalPackagesFolderIdentity);

internal sealed class IntegrationPackageRestoreConfiguration(
    TemporaryNuGetConfigFile? policyOverlay,
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
    string? PackageSourceAlias,
    string[] SensitiveSources,
    string? GlobalPackagesFolder);
