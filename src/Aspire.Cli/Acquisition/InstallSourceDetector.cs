// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Identifies the running CLI's install source without modifying the installation.
/// </summary>
internal sealed class InstallSourceDetector(
    IProcessPathProvider processPathProvider,
    IInstallSidecarReader sidecarReader,
    IWindowsRegistryReader registryReader,
    IEnvironment environment,
    ILogger<InstallSourceDetector> logger)
{
    public string Detect()
    {
        try
        {
            var processPath = processPathProvider.ProcessPath;
            if (string.IsNullOrEmpty(processPath) || !Path.IsPathFullyQualified(processPath))
            {
                logger.LogDebug("Install-source detection skipped because the process path is unavailable or not absolute.");
                return "unknown";
            }

            var resolvedPath = CliPathHelper.ResolveSymlinkToFullPath(processPath, logger);
            if (resolvedPath is null)
            {
                return "unknown";
            }

            var binaryDirectory = Path.GetDirectoryName(resolvedPath);
            var comparison = environment.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.IsNullOrEmpty(binaryDirectory) ||
                !string.Equals(Path.GetFileName(resolvedPath), environment.IsWindows() ? "aspire.exe" : "aspire", comparison))
            {
                return "unknown";
            }

            if (sidecarReader.TryRead(binaryDirectory) is InstallSidecarReadResult.Ok sidecar &&
                sidecar.Info.Source.ToWireString() is { } source)
            {
                // Never report RawSource: sidecars can contain arbitrary strings, but telemetry
                // must stay within the known install-source vocabulary.
                return source;
            }

            // WinGet does not stamp a sidecar until bundle extraction or doctor runs. Read the
            // registry directly so even the first --version invocation works on a read-only install.
            if (environment.IsWindows() && registryReader.HasWingetAspireUninstallEntry(resolvedPath))
            {
                return InstallSourceExtensions.WingetWire;
            }

            if (NpmInstallDetection.IsNpmPackage(environment.GetEnvironmentVariable(NpmInstallDetection.PackageEnvironmentVariableName)))
            {
                return "npm";
            }

            if (IsMiseInstall(binaryDirectory, comparison))
            {
                return "mise";
            }

            if (DotNetToolDetection.IsRunningAsDotNetTool(resolvedPath))
            {
                return InstallSourceExtensions.DotnetToolWire;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            logger.LogDebug(ex, "Could not detect the CLI install source.");
        }

        return "unknown";
    }

    private bool IsMiseInstall(string binaryDirectory, StringComparison comparison)
    {
        // mise uses <data>/installs/aspire/<version>/aspire (or <version>/bin/aspire).
        // Match the tool and directory boundaries, not merely a "mise" substring or the
        // presence of MISE_* variables in a shell that could launch an unrelated CLI.
        // https://mise.jdx.dev/directories.html
        var directory = new DirectoryInfo(binaryDirectory);
        var versionDirectory = string.Equals(directory.Name, "bin", comparison) ? directory.Parent : directory;
        var toolDirectory = versionDirectory?.Parent;
        var installsDirectory = toolDirectory?.Parent;
        if (toolDirectory is null || installsDirectory is null ||
            !(string.Equals(toolDirectory.Name, "aspire", comparison) ||
              string.Equals(toolDirectory.Name, "github-microsoft-aspire", comparison)))
        {
            return false;
        }

        // This also covers XDG_DATA_HOME, Windows LOCALAPPDATA, and system installs.
        if (string.Equals(installsDirectory.Name, "installs", comparison) &&
            string.Equals(installsDirectory.Parent?.Name, "mise", comparison))
        {
            return true;
        }

        return MatchesConfiguredDirectory(installsDirectory.FullName, environment.GetEnvironmentVariable("MISE_INSTALLS_DIR"), comparison) ||
            MatchesConfiguredDirectory(installsDirectory.FullName, environment.GetEnvironmentVariable("MISE_SYSTEM_INSTALLS_DIR"), comparison) ||
            (string.Equals(installsDirectory.Name, "installs", comparison) &&
             MatchesConfiguredDirectory(installsDirectory.Parent?.FullName, environment.GetEnvironmentVariable("MISE_DATA_DIR"), comparison));
    }

    private bool MatchesConfiguredDirectory(string? actualDirectory, string? configuredDirectory, StringComparison comparison)
    {
        // Use the executable's canonicalization for the configured root too, including
        // macOS aliases such as /private/tmp/tools and /tmp/tools.
        return actualDirectory is not null &&
            !string.IsNullOrWhiteSpace(configuredDirectory) &&
            Path.IsPathFullyQualified(configuredDirectory) &&
            CliPathHelper.ResolveSymlinkToFullPath(Path.TrimEndingDirectorySeparator(configuredDirectory), logger) is { } resolvedDirectory &&
            string.Equals(
                actualDirectory,
                Path.TrimEndingDirectorySeparator(resolvedDirectory),
                comparison);
    }
}
