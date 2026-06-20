// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Gets the version of the Aspire CLI executable.
/// </summary>
public sealed class GetAspireCliVersion : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Path to the Aspire CLI executable.
    /// </summary>
    [Required]
    public string? AspireCliPath { get; set; }

    /// <summary>
    /// The resolved Aspire CLI version (e.g., "13.5.0" or "13.5.0-preview.1.26319.9").
    /// Empty string if the version cannot be determined.
    /// </summary>
    [Output]
    public string AspireCliVersion { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(AspireCliPath) || !File.Exists(AspireCliPath))
        {
            Log.LogMessage(MessageImportance.Low, "Aspire CLI path is empty or does not exist.");
            return true;
        }

        try
        {
            var version = GetCliVersion(AspireCliPath);
            AspireCliVersion = version ?? string.Empty;

            if (!string.IsNullOrEmpty(version))
            {
                Log.LogMessage(MessageImportance.Low, $"Resolved Aspire CLI version: {version}");
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "Could not determine Aspire CLI version.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogMessage(MessageImportance.Low, $"Error getting Aspire CLI version: {ex.Message}");
            return true;
        }
    }

    private static string? GetCliVersion(string cliPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Use a compatible approach for both net472 and net8.0+
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
#if NET8_0_OR_GREATER
            try
            {
                var waitTask = process.WaitForExitAsync(cts.Token);
                waitTask.Wait();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
#else
            try
            {
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
#endif

            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();

            // Parse version from output like "aspire version 13.5.0" or just "13.5.0"
            // Handle both "aspire version 13.5.0-preview.1.26319.9" and "13.5.0"
            var versionMatch = Regex.Match(output, @"(\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?)");
            return versionMatch.Success ? versionMatch.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

