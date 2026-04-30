// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.OpenCode;

/// <summary>
/// Runs OpenCode CLI commands.
/// </summary>
/// <param name="logger">The logger for diagnostic output.</param>
internal sealed class OpenCodeCliRunner(ILogger<OpenCodeCliRunner> logger) : IOpenCodeCliRunner
{
    /// <inheritdoc />
    public async Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Checking for OpenCode CLI installation");

        try
        {
            var startInfo = new ProcessStartInfo("opencode", "--version")
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
                logger.LogDebug("OpenCode CLI returned non-zero exit code {ExitCode}: {Error}", process.ExitCode, errorOutput.Trim());
                return null;
            }

            var output = await outputTask.ConfigureAwait(false);
            var versionString = output.Trim();

            if (string.IsNullOrEmpty(versionString))
            {
                logger.LogDebug("OpenCode CLI returned empty version output");
                return null;
            }

            // Try to parse the version string (may have a 'v' prefix like "v1.2.3")
            if (versionString.StartsWith('v') || versionString.StartsWith('V'))
            {
                versionString = versionString[1..];
            }

            if (SemVersion.TryParse(versionString, SemVersionStyles.Any, out var version))
            {
                logger.LogDebug("Found OpenCode CLI version: {Version}", version);
                return version;
            }

            logger.LogDebug("Could not parse OpenCode CLI version from output: {Output}", output.Trim());
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "OpenCode CLI is not installed or not found in PATH");
            return null;
        }
    }
}
