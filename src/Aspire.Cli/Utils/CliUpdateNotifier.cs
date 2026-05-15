// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Utils;

internal interface ICliUpdateNotifier
{
    Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken);
    Task<CliVersionStatus> GetVersionStatusAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken);
    void NotifyIfUpdateAvailable();
    bool IsUpdateAvailable();
}

internal sealed record CliVersionStatus(
    string? CurrentVersion,
    string? LatestVersion,
    string? UpdateCommand,
    string? UpdateCheckError = null,
    string? LatestVersionChannel = null);

/// <summary>
/// Coarse-grained labels for the channel a recommended CLI update is being
/// pulled from. <see cref="PackageUpdateHelpers.GetNewerVersion"/> picks
/// between <c>newestStable</c> and <c>newestPrerelease</c> when computing
/// the recommendation, so labelling by stable vs prerelease is faithful to
/// the underlying decision rule. We deliberately don't try to distinguish
/// staging from daily here — the version string alone can't reliably do so,
/// and the user-visible doctor message only needs to convey "where to
/// look", not the specific feed identity.
/// </summary>
internal static class PackageUpdateRecommendationChannels
{
    public const string Stable = "stable";
    public const string Prerelease = "prerelease";
}

internal class CliUpdateNotifier(
    ILogger<CliUpdateNotifier> logger,
    INuGetPackageCache nuGetPackageCache,
    IInteractionService interactionService,
    IInstallationDiscovery installationDiscovery,
    IUpgradeInstructionProvider upgradeInstructionProvider,
    CliExecutionContext executionContext,
    WingetFirstRunProbe wingetFirstRunProbe) : ICliUpdateNotifier
{
    private IEnumerable<Shared.NuGetPackageCli>? _availablePackages;

    public async Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        _availablePackages = await GetCliPackagesAsync(workingDirectory, cancellationToken);
    }

    public void NotifyIfUpdateAvailable()
    {
        var status = GetCachedVersionStatus();
        if (status.LatestVersion is not null)
        {
            interactionService.DisplayVersionUpdateNotification(status.LatestVersion, status.UpdateCommand);
        }
    }

    public async Task<CliVersionStatus> GetVersionStatusAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            // Callers that need a synchronous answer cannot rely on the background
            // prefetcher racing to populate the cache before command exit.
            // Refresh through the same method used by background update notifications so
            // NuGet source selection and cache mutation stay consistent.
            await CheckForCliUpdatesAsync(workingDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to check for Aspire CLI updates.");
            return GetCachedVersionStatus(ex.Message);
        }

        return GetCachedVersionStatus();
    }

    public bool IsUpdateAvailable()
        => GetCachedVersionStatus().LatestVersion is not null;

    protected virtual SemVersion? GetCurrentVersion()
    {
        return PackageUpdateHelpers.GetCurrentPackageVersion();
    }

    private CliVersionStatus GetCachedVersionStatus(string? updateCheckError = null)
    {
        // Keep all version comparison and update-command selection in one place so
        // callers cannot disagree when package metadata has already been fetched.
        var currentVersion = GetCurrentVersion();
        var currentVersionString = currentVersion?.ToString() ?? PackageUpdateHelpers.GetCurrentAssemblyVersion();

        if (updateCheckError is not null)
        {
            return new CliVersionStatus(currentVersionString, null, null, updateCheckError);
        }

        if (_availablePackages is null)
        {
            return new CliVersionStatus(currentVersionString, null, null);
        }

        if (currentVersion is null)
        {
            logger.LogDebug("Unable to determine current CLI version for update check.");
            return new CliVersionStatus(currentVersionString, null, null);
        }

        var newerVersion = PackageUpdateHelpers.GetNewerVersion(logger, currentVersion, _availablePackages);
        var updateCommand = newerVersion is null ? null : GetRouteAwareUpdateCommand();
        // Derive the lane the recommendation comes from so doctor can show
        // 'Latest version is X (channel: stable)' vs '(channel: prerelease)'.
        // GetNewerVersion picks between newestStable and newestPrerelease
        // by exactly this rule, so re-classifying from the returned
        // version's prerelease flag is faithful to the decision the
        // package helper made.
        var latestChannel = newerVersion is null
            ? null
            : (newerVersion.IsPrerelease ? PackageUpdateRecommendationChannels.Prerelease : PackageUpdateRecommendationChannels.Stable);
        return new CliVersionStatus(currentVersionString, newerVersion?.ToString(), updateCommand, UpdateCheckError: null, LatestVersionChannel: latestChannel);
    }

    /// <summary>
    /// Returns the route-appropriate command to recommend in the
    /// "version X available" notification. For script-route installs we
    /// suggest <c>aspire update --self</c>. For every other route, including
    /// Unknown, we defer to <see cref="IUpgradeInstructionProvider"/> so
    /// users see the command or refusal hint that matches how they installed
    /// the CLI (winget upgrade, brew upgrade --cask, dotnet tool update,
    /// get-aspire-cli-pr, etc.).
    /// </summary>
    /// <remarks>
    /// When the initial discovery reports no route (i.e., no sidecar on disk
    /// yet), runs <see cref="WingetFirstRunProbe"/> on the binary directory
    /// derived from the discovery's canonical path, then re-describes so the
    /// freshly-stamped sidecar is picked up. The probe self-gates via
    /// <see cref="IWindowsRegistryReader"/>, so the call is a cheap no-op
    /// on non-Windows / non-WinGet installs.
    /// </remarks>
    private string GetRouteAwareUpdateCommand()
    {
        var info = installationDiscovery.DescribeSelf();
        var canonicalPath = info.CanonicalPath ?? info.Path;

        if (string.IsNullOrEmpty(info.Route) && !string.IsNullOrEmpty(canonicalPath))
        {
            var binaryDir = Path.GetDirectoryName(canonicalPath);
            if (!string.IsNullOrEmpty(binaryDir))
            {
                wingetFirstRunProbe.Run(binaryDir);
                info = installationDiscovery.DescribeSelf();
                canonicalPath = info.CanonicalPath ?? info.Path;
            }
        }

        var source = InstallSourceExtensions.ParseInstallSource(info.Route);

        // Legacy fallback for pre-sidecar dotnet-tool installs (mirrors the
        // UpdateCommand --self resolution rule). Uses the no-arg overload so
        // the AsyncLocal test override is honored.
        if (source == InstallSource.Unknown && DotNetToolDetection.IsRunningAsDotNetTool())
        {
            source = InstallSource.DotnetTool;
        }

        return upgradeInstructionProvider.GetUpdateCommand(source, canonicalPath, executionContext.IdentityChannel)
            ?? "aspire update --self";
    }

    private async Task<IEnumerable<Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        return await nuGetPackageCache.GetCliPackagesAsync(
            workingDirectory: workingDirectory,
            prerelease: true,
            nugetConfigFile: null,
            cancellationToken: cancellationToken);
    }
}
