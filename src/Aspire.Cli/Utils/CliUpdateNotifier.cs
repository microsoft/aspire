// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

internal sealed record CliVersionStatus(string? CurrentVersion, string? LatestVersion, string? UpdateCommand, string? UpdateCheckError = null);

internal class CliUpdateNotifier(
    ILogger<CliUpdateNotifier> logger,
    INuGetPackageCache nuGetPackageCache,
    IInteractionService interactionService) : ICliUpdateNotifier
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
        var updateCommand = newerVersion is null ? null : DotNetToolDetection.GetDotNetToolUpdateCommand() ?? "aspire update";
        return new CliVersionStatus(currentVersionString, newerVersion?.ToString(), updateCommand);
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
