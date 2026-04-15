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
                ? "dotnet tool update -g Aspire.Cli"
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
    /// <c>true</c> if running as a .NET tool (process name is "dotnet" or "dotnet.exe"); 
    /// <c>false</c> if running as a native binary (process name is "aspire" or "aspire.exe") or if the process path cannot be determined.
    /// </returns>
    /// <remarks>
    /// This detection is used to determine which update command to display to users:
    /// <list type="bullet">
    /// <item>.NET tool installation: "dotnet tool update -g Aspire.Cli"</item>
    /// <item>Native binary installation: "aspire update --self"</item>
    /// </list>
    /// The detection works by examining <see cref="Environment.ProcessPath"/>, which returns the full path to the current executable.
    /// When running as a .NET tool, this path points to the dotnet host executable. When running as a native binary, 
    /// it points to the aspire executable itself.
    /// </remarks>
    private static bool IsRunningAsDotNetTool()
    {
        // When running as a dotnet tool, the process path points to "dotnet" or "dotnet.exe"
        // When running as a native binary, it points to "aspire" or "aspire.exe"
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    protected virtual SemVersion? GetCurrentVersion()
    {
        return PackageUpdateHelpers.GetCurrentPackageVersion();
    }
}
