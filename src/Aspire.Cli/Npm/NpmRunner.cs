// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Npm;

/// <summary>
/// Runs npm CLI commands for package management operations.
/// </summary>
internal sealed class NpmRunner(ILogger<NpmRunner> logger) : INpmRunner
{
    /// <inheritdoc />
    public async Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return null;
        }

        // Use an isolated temp subdirectory so npm doesn't pick up .npmrc or
        // other config files from the shared temp root or the user's CWD.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            // Resolve version: npm view <package>@<range> version
            var versionOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["view", $"{packageName}@{versionRange}", "version"],
                tempDir,
                cancellationToken);

            if (versionOutput is null)
            {
                return null;
            }

            var versionString = versionOutput.Trim();
            if (!SemVersion.TryParse(versionString, SemVersionStyles.Any, out var version))
            {
                logger.LogDebug("Could not parse npm version from output: {Output}", versionString);
                return null;
            }

            // Resolve integrity hash: npm view <package>@<version> dist.integrity
            var integrityOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["view", $"{packageName}@{version}", "dist.integrity"],
                tempDir,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(integrityOutput))
            {
                logger.LogDebug("Could not resolve integrity hash for {Package}@{Version}", packageName, version);
                return null;
            }

            return new NpmPackageInfo
            {
                Version = version,
                Integrity = integrityOutput.Trim()
            };
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    /// <inheritdoc />
    public async Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return null;
        }

        var output = await RunNpmCommandInDirectoryAsync(
            npmPath,
            ["pack", $"{packageName}@{version}", "--pack-destination", outputDirectory],
            outputDirectory,
            cancellationToken);

        if (output is null)
        {
            return null;
        }

        // npm pack outputs the filename of the created tarball
        var filename = output.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(filename))
        {
            logger.LogDebug("npm pack returned empty filename");
            return null;
        }

        var tarballPath = Path.Combine(outputDirectory, filename);
        if (!File.Exists(tarballPath))
        {
            logger.LogDebug("npm pack output file not found: {Path}", tarballPath);
            return null;
        }

        return tarballPath;
    }

    /// <inheritdoc />
    public async Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return false;
        }

        // npm audit signatures requires a project context (node_modules + package-lock.json).
        // For global tool installs there is no project, so we create a temporary one.
        // The package must be installed from the registry (not a local tarball) because
        // npm audit signatures skips packages with "resolved: file:..." in the lockfile.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            // Create minimal package.json
            var packageJson = Path.Combine(tempDir, "package.json");
            await File.WriteAllTextAsync(
                packageJson,
                """{"name":"aspire-verify","version":"1.0.0","private":true}""",
                cancellationToken).ConfigureAwait(false);

            // Install the package from the registry to get proper attestation metadata
            var installOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["install", $"{packageName}@{version}", "--ignore-scripts"],
                tempDir,
                cancellationToken);

            if (installOutput is null)
            {
                logger.LogDebug("Failed to install {Package}@{Version} into temporary project for audit", packageName, version);
                return false;
            }

            // Run npm audit signatures in the temporary project directory
            var auditOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["audit", "signatures"],
                tempDir,
                cancellationToken);

            return auditOutput is not null;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    /// <inheritdoc />
    public async Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return false;
        }

        // Use an isolated temp subdirectory so npm doesn't pick up .npmrc or
        // other config files from the shared temp root or the user's CWD.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            var output = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["install", "-g", tarballPath],
                tempDir,
                cancellationToken);

            return output is not null;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    private string? FindNpmPath()
    {
        var npmPath = PathLookupHelper.FindFullPathFromPath("npm");
        if (npmPath is null)
        {
            logger.LogDebug("npm is not installed or not found in PATH");
        }

        return npmPath;
    }

    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-npm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private void CleanupTempDirectory(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Failed to clean up temporary directory: {TempDir}", tempDir);
        }
    }

    private async Task<string?> RunNpmCommandInDirectoryAsync(string npmPath, string[] args, string workingDirectory, CancellationToken cancellationToken)
    {
        var argsString = string.Join(" ", args);
        logger.LogDebug("Running npm {Args} in {WorkingDirectory}", argsString, workingDirectory);

        try
        {
            var startInfo = new ProcessStartInfo(npmPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorOutput = await errorTask.ConfigureAwait(false);
                logger.LogDebug("npm {Args} returned non-zero exit code {ExitCode}: {Error}", argsString, process.ExitCode, errorOutput.Trim());
                return null;
            }

            return await outputTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Failed to run npm {Args}", argsString);
            return null;
        }
    }

}
