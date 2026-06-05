// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if the deprecated 'aspire' workload is installed.
/// </summary>
/// <remarks>
/// The 'aspire' workload has been deprecated and should be uninstalled.
/// Users with this workload installed may encounter conflicts or confusion.
/// </remarks>
internal sealed class DeprecatedWorkloadCheck(ILogger<DeprecatedWorkloadCheck> logger) : IEnvironmentCheck
{
    private static readonly TimeSpan s_processTimeout = TimeSpan.FromSeconds(10);

    public int Order => 32; // After SDK check (30), before dev certs (35)

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "workload list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                logger.LogDebug("Failed to start dotnet workload list process");
                // Don't fail the check if we can't run the command - the SDK check will catch SDK issues
                return [];
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(s_processTimeout);

            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                logger.LogDebug("dotnet workload list timed out");
                return [];
            }

            if (process.ExitCode != 0)
            {
                logger.LogDebug("dotnet workload list exited with code {ExitCode}", process.ExitCode);
                return [];
            }

            // Check if the deprecated 'aspire' workload is installed
            // The output format is typically:
            // Installed Workload Id      Manifest Version       Installation Source
            // --------------------------------------------------------------------
            // aspire                     8.0.0/8.0.100          SDK 8.0.100
            if (IsAspireWorkloadInstalled(output))
            {
                return [new EnvironmentCheckResult
                {
                    Category = "sdk",
                    Name = "aspire-workload",
                    Status = EnvironmentCheckStatus.Fail,
                    Message = "Deprecated 'aspire' workload is installed",
                    Details = "The 'aspire' workload has been deprecated and causes conflicts with modern Aspire projects.",
                    Fix = "Run: dotnet workload uninstall aspire",
                    Link = "https://aka.ms/aspire-prerequisites"
                }];
            }

            // Workload not installed - this is the expected state, no need to report anything
            return [];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking for aspire workload");
            // Don't fail the check if we can't verify - just skip it
            return [];
        }
    }

    /// <summary>
    /// Checks if the 'aspire' workload is present in the output of 'dotnet workload list'.
    /// </summary>
    internal static bool IsAspireWorkloadInstalled(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        // Parse each line looking for the 'aspire' workload
        // The format is whitespace-separated columns, with the first column being the workload ID
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Skip header lines, separator lines, and informational lines
            if (trimmedLine.StartsWith("Installed", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("Workload version:", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("---") ||
                trimmedLine.StartsWith("Use", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            // The workload ID is the first column
            var columns = trimmedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > 0 &&
                string.Equals(columns[0], "aspire", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
