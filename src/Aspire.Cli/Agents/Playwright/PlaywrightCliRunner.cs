// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Agents.Playwright;

/// <summary>
/// Runs playwright-cli commands.
/// </summary>
internal sealed class PlaywrightCliRunner(ILogger<PlaywrightCliRunner> logger) : IPlaywrightCliRunner
{
    /// <inheritdoc />
    public async Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var executablePath = PathLookupHelper.FindFullPathFromPath("playwright-cli");
        if (executablePath is null)
        {
            logger.LogDebug("playwright-cli is not installed or not found in PATH");
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorOutput = await errorTask.ConfigureAwait(false);
                logger.LogDebug("playwright-cli --version returned non-zero exit code {ExitCode}: {Error}", process.ExitCode, errorOutput.Trim());
                return null;
            }

            var output = await outputTask.ConfigureAwait(false);
            var versionString = output.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

            if (string.IsNullOrEmpty(versionString))
            {
                logger.LogDebug("playwright-cli returned empty version output");
                return null;
            }

            if (versionString.StartsWith('v') || versionString.StartsWith('V'))
            {
                versionString = versionString[1..];
            }

            if (SemVersion.TryParse(versionString, SemVersionStyles.Any, out var version))
            {
                logger.LogDebug("Found playwright-cli version: {Version}", version);
                return version;
            }

            logger.LogDebug("Could not parse playwright-cli version from output: {Output}", versionString);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "playwright-cli is not installed or not found in PATH");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var executablePath = PathLookupHelper.FindFullPathFromPath("playwright-cli");
        if (executablePath is null)
        {
            logger.LogDebug("playwright-cli is not installed or not found in PATH");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            startInfo.ArgumentList.Add("install");
            startInfo.ArgumentList.Add("--skills");

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorOutput = await errorTask.ConfigureAwait(false);
                logger.LogDebug("playwright-cli install --skills returned non-zero exit code {ExitCode}: {Error}", process.ExitCode, errorOutput.Trim());
                return false;
            }

            var output = await outputTask.ConfigureAwait(false);
            logger.LogDebug("playwright-cli install --skills output: {Output}", output.Trim());
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Failed to run playwright-cli install --skills");
            return false;
        }
        finally
        {
            // playwright-cli install --skills may leave behind an empty .playwright
            // directory in the working directory. Clean it up if it exists and is empty.
            CleanupEmptyPlaywrightDirectory(workingDirectory);
        }
    }

    private void CleanupEmptyPlaywrightDirectory(string workingDirectory)
    {
        try
        {
            var playwrightDir = Path.Combine(workingDirectory, ".playwright");
            if (Directory.Exists(playwrightDir) && Directory.GetFileSystemEntries(playwrightDir).Length == 0)
            {
                Directory.Delete(playwrightDir);
                logger.LogDebug("Removed empty .playwright directory from {WorkingDirectory}", workingDirectory);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Failed to clean up .playwright directory in {WorkingDirectory}", workingDirectory);
        }
    }
}
