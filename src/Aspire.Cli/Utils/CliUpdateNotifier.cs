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
    void NotifyIfUpdateAvailable();
    bool IsUpdateAvailable();
}

internal class CliUpdateNotifier(
    ILogger<CliUpdateNotifier> logger,
    INuGetPackageCache nuGetPackageCache,
    IInteractionService interactionService) : ICliUpdateNotifier
{
    private IEnumerable<Shared.NuGetPackageCli>? _availablePackages;

    public async Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        _availablePackages = await nuGetPackageCache.GetCliPackagesAsync(
            workingDirectory: workingDirectory,
            prerelease: true,
            nugetConfigFile: null,
            cancellationToken: cancellationToken);
    }

    public void NotifyIfUpdateAvailable()
    {
        if (_availablePackages is null)
        {
            return;
        }

        var currentVersion = GetCurrentVersion();
        if (currentVersion is null)
        {
            logger.LogDebug("Unable to determine current CLI version for update check.");
            return;
        }

        var newerVersion = PackageUpdateHelpers.GetNewerVersion(logger, currentVersion, _availablePackages);

        if (newerVersion is not null)
        {
            var updateCommand = DotNetToolDetection.GetDotNetToolUpdateCommand() ?? "aspire update";

            interactionService.DisplayVersionUpdateNotification(newerVersion.ToString(), updateCommand);
        }
    }

    public bool IsUpdateAvailable()
    {
        if (_availablePackages is null)
        {
            return false;
        }

        var currentVersion = GetCurrentVersion();
        if (currentVersion is null)
        {
            return false;
        }

        var newerVersion = PackageUpdateHelpers.GetNewerVersion(logger, currentVersion, _availablePackages);
        return newerVersion is not null;
    }

    protected virtual SemVersion? GetCurrentVersion()
    {
        return PackageUpdateHelpers.GetCurrentPackageVersion();
    }
}
