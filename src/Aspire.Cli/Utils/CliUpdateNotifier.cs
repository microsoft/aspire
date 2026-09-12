// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
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
/// the underlying decision rule. The npm path resolves the single version
/// behind the <c>latest</c> dist-tag and classifies it by the same rule.
/// We deliberately don't try to distinguish staging from daily here — the
/// version string alone can't reliably do so, and the user-visible doctor
/// message only needs to convey "where to look", not the specific feed
/// identity.
/// </summary>
internal static class PackageUpdateRecommendationChannels
{
    public const string Stable = "stable";
    public const string Prerelease = "prerelease";
}

internal class CliUpdateNotifier(
    ILogger<CliUpdateNotifier> logger,
    INuGetPackageCache nuGetPackageCache,
    INpmRunner npmRunner,
    IInteractionService interactionService,
    IProcessPathProvider processPathProvider,
    CliExecutionContext executionContext) : ICliUpdateNotifier
{
    private IEnumerable<Shared.NuGetPackageCli>? _availablePackages;
    private SemVersion? _availableNpmVersion;

    public async Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        // An npm-installed CLI updates with "npm install -g @microsoft/aspire-cli@latest", so the
        // recommendation has to come from the same dist-tag that command resolves. Querying NuGet
        // here would advertise a version npm cannot install.
        if (NpmInstallDetection.IsRunningFromNpm())
        {
            _availablePackages = null;
            _availableNpmVersion = null;
            _availableNpmVersion = await npmRunner.GetLatestVersionAsync(
                NpmInstallDetection.ExpectedPackageName,
                cancellationToken);
            return;
        }

        _availableNpmVersion = null;
        _availablePackages = await GetCliPackagesAsync(workingDirectory, cancellationToken);
    }

    public void NotifyIfUpdateAvailable()
    {
        ValidateCliPackageMetadataPrefetching();
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
    {
        ValidateCliPackageMetadataPrefetching();
        return GetCachedVersionStatus().LatestVersion is not null;
    }

    [Conditional("DEBUG")]
    private void ValidateCliPackageMetadataPrefetching()
    {
        if (executionContext.Command is BaseCommand { PrefetchesCliPackageMetadata: false } command)
        {
            throw new PackageMetadataPrefetchingValidationException($"Command '{command.Name}' consumes cached CLI package metadata but does not enable {nameof(BaseCommand.PrefetchesCliPackageMetadata)}.");
        }
    }

    protected virtual SemVersion? GetCurrentVersion()
    {
        // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
        // the update check compares the ACTUAL installed binary against the latest available
        // package to decide whether to recommend an update, so it must read the real assembly
        // version rather than an emulated ASPIRE_CLI_VERSION identity.
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

        if (_availablePackages is null && _availableNpmVersion is null)
        {
            return new CliVersionStatus(currentVersionString, null, null);
        }

        if (currentVersion is null)
        {
            logger.LogDebug("Unable to determine current CLI version for update check.");
            return new CliVersionStatus(currentVersionString, null, null);
        }

        var newerVersion = _availableNpmVersion is { } latestNpmVersion
            ? GetNewerNpmVersion(currentVersion, latestNpmVersion)
            : PackageUpdateHelpers.GetNewerVersion(logger, currentVersion, _availablePackages!);
        var updateCommand = newerVersion is null
            ? null
            : DotNetToolDetection.GetDotNetToolUpdateCommand(processPathProvider.ProcessPath)
                ?? NpmInstallDetection.GetNpmUpdateCommand()
                ?? "aspire update";
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

    private SemVersion? GetNewerNpmVersion(SemVersion currentVersion, SemVersion latestVersion)
    {
        // npm's "latest" dist-tag names one concrete version rather than a candidate set, so there
        // is no stable/prerelease selection to make here — only a precedence comparison. Precedence
        // ordering means a prerelease of the running version (9.4.0-preview.1 against 9.4.0) sorts
        // lower and is correctly not offered as an update.
        if (SemVersion.PrecedenceComparer.Compare(currentVersion, latestVersion) >= 0)
        {
            logger.LogDebug("No newer CLI version is available from npm. Current: {CurrentVersion}, latest: {LatestVersion}.", currentVersion, latestVersion);
            return null;
        }

        logger.LogDebug("Newer CLI version available from npm: {CurrentVersion} -> {LatestVersion}.", currentVersion, latestVersion);
        return latestVersion;
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
