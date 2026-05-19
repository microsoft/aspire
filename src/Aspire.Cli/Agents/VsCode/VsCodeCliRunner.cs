// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Runs VS Code CLI commands.
/// </summary>
/// <param name="logger">The logger for diagnostic output.</param>
internal sealed class VsCodeCliRunner(ILogger<VsCodeCliRunner> logger) : IVsCodeCliRunner
{
    /// <inheritdoc />
    public async Task<SemVersion?> GetVersionAsync(VsCodeRunOptions options, CancellationToken cancellationToken)
    {
        var command = options.UseInsiders ? "code-insiders" : "code";
        logger.LogDebug("Checking for VS Code CLI installation using command: {Command}", command);

        var executablePath = PathLookupHelper.FindFullPathFromPath(command);
        if (executablePath is null)
        {
            logger.LogDebug("VS Code CLI ({Command}) is not installed or not found in PATH", command);
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
                logger.LogDebug("VS Code CLI ({Command}) returned non-zero exit code {ExitCode}: {Error}", command, process.ExitCode, errorOutput.Trim());
                return null;
            }

            var output = await outputTask.ConfigureAwait(false);

            if (string.IsNullOrEmpty(output))
            {
                logger.LogDebug("VS Code CLI ({Command}) returned empty version output", command);
                return null;
            }

            // The output from `code --version` and `code-insiders --version` is a multi-line string
            // The first line is a semantic version number (e.g., "1.85.0")
            var lines = output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                logger.LogDebug("VS Code CLI ({Command}) returned no lines in output", command);
                return null;
            }

            var versionString = lines[0].Trim();

            // Try to parse the version string (may have a 'v' prefix like "v1.2.3")
            if (versionString.StartsWith('v') || versionString.StartsWith('V'))
            {
                versionString = versionString[1..];
            }

            if (SemVersion.TryParse(versionString, SemVersionStyles.Any, out var version))
            {
                logger.LogDebug("Found VS Code CLI ({Command}) version: {Version}", command, version);
                return version;
            }

            logger.LogDebug("Could not parse VS Code CLI ({Command}) version from output: {Output}", command, output.Trim());
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "VS Code CLI ({Command}) is not installed or not found in PATH", command);
            return null;
        }
    }
}
