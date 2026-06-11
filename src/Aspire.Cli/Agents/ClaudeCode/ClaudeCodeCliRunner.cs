// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Agents.ClaudeCode;

/// <summary>
/// Runs Claude Code CLI commands.
/// </summary>
/// <param name="logger">The logger for diagnostic output.</param>
internal sealed class ClaudeCodeCliRunner(ILogger<ClaudeCodeCliRunner> logger) : IClaudeCodeCliRunner
{
    /// <inheritdoc />
    public async Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Checking for Claude Code CLI installation");

        var executablePath = PathLookupHelper.FindFullPathFromPath("claude");
        if (executablePath is null)
        {
            logger.LogDebug("Claude Code CLI is not installed or not found in PATH");
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
                logger.LogDebug("Claude Code CLI returned non-zero exit code {ExitCode}: {Error}", process.ExitCode, errorOutput.Trim());
                return null;
            }

            var output = await outputTask.ConfigureAwait(false);
            var versionString = output.Trim();

            if (string.IsNullOrEmpty(versionString))
            {
                logger.LogDebug("Claude Code CLI returned empty version output");
                return null;
            }

            // Version output format is "2.0.52 (Claude Code)" - extract just the version part
            var spaceIndex = versionString.IndexOf(' ');
            if (spaceIndex > 0)
            {
                versionString = versionString[..spaceIndex];
            }

            // Try to parse the version string (may have a 'v' prefix like "v1.2.3")
            if (versionString.StartsWith('v') || versionString.StartsWith('V'))
            {
                versionString = versionString[1..];
            }

            if (SemVersion.TryParse(versionString, SemVersionStyles.Any, out var version))
            {
                logger.LogDebug("Found Claude Code CLI version: {Version}", version);
                return version;
            }

            logger.LogDebug("Could not parse Claude Code CLI version from output: {Output}", output.Trim());
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Claude Code CLI is not installed or not found in PATH");
            return null;
        }
    }
}
