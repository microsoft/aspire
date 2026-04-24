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
            var updateCommand = IsRunningAsDotNetTool()
                ? "dotnet tool update Aspire.Cli"
                : "aspire update";

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

    /// <summary>
    /// Determines whether the Aspire CLI is running as a .NET tool or as a native binary.
    /// </summary>
    /// <returns>
    /// <c>true</c> if running as a .NET tool (process hosted by "dotnet" or inside a .store directory);
    /// <c>false</c> if running as a standalone native binary or if the process path cannot be determined.
    /// </returns>
    /// <remarks>
    /// This detection is used to determine which update command to display to users:
    /// <list type="bullet">
    /// <item>.NET tool installation: "dotnet tool update Aspire.Cli"</item>
    /// <item>Native binary installation: "aspire update --self"</item>
    /// </list>
    /// The detection works by examining <see cref="Environment.ProcessPath"/>:
    /// <list type="bullet">
    /// <item>Managed tools: the dotnet host executable runs the tool DLL, so ProcessPath is "dotnet".</item>
    /// <item>NativeAOT tools: the binary executes directly from the .store directory, so ProcessPath
    ///       is inside <c>.store/aspire.cli/...</c>.</item>
    /// </list>
    /// </remarks>
    private static bool IsRunningAsDotNetTool()
        => DotNetToolDetection.IsRunningAsDotNetTool(Environment.ProcessPath);

    protected virtual SemVersion? GetCurrentVersion()
    {
        return PackageUpdateHelpers.GetCurrentPackageVersion();
    }
}
