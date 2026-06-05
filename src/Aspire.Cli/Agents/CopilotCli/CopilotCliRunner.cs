// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Agents.CopilotCli;

/// <summary>
/// Runs GitHub Copilot CLI commands.
/// </summary>
/// <param name="logger">The logger for diagnostic output.</param>
internal sealed class CopilotCliRunner(ILogger<CopilotCliRunner> logger) : ICopilotCliRunner
{
    /// <inheritdoc />
    public async Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Checking for GitHub Copilot CLI installation");

        var executablePath = PathLookupHelper.FindFullPathFromPath("copilot");
        if (executablePath is null)
        {
            logger.LogDebug("GitHub Copilot CLI is not installed or not found in PATH");
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
                logger.LogDebug("GitHub Copilot CLI returned non-zero exit code {ExitCode}: {Error}", process.ExitCode, errorOutput.Trim());
                return null;
            }

            var output = await outputTask.ConfigureAwait(false);

            if (TryParseVersionOutput(output, out var version))
            {
                logger.LogDebug("Found GitHub Copilot CLI version: {Version}", version);
                return version;
            }

            logger.LogDebug("Could not parse GitHub Copilot CLI version from output: {Output}", output.Trim());
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "GitHub Copilot CLI is not installed or not found in PATH");
            return null;
        }
    }

    internal static bool TryParseVersionOutput(string output, out SemVersion? version)
    {
        version = null;
        var versionString = output.Trim();

        if (string.IsNullOrEmpty(versionString))
        {
            return false;
        }

        // Version output may be on the first line if multi-line
        var lines = versionString.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0)
        {
            versionString = lines[0].Trim();
        }

        // Try to extract the version from known formats like "GitHub Copilot CLI 0.0.397"
        var lastSpaceIndex = versionString.LastIndexOf(' ');
        if (lastSpaceIndex >= 0)
        {
            versionString = versionString[(lastSpaceIndex + 1)..];
        }

        // Trim common trailing punctuation that may follow the version (for example, "0.0.397.")
        versionString = versionString.TrimEnd('.', ',', ')');

        // Try to parse the version string (may have a 'v' prefix like "v1.2.3")
        if (versionString.StartsWith('v') || versionString.StartsWith('V'))
        {
            versionString = versionString[1..];
        }

        return SemVersion.TryParse(versionString, SemVersionStyles.Any, out version);
    }
}
