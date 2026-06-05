// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if running in WSL environment and detects potential issues.
/// </summary>
internal sealed class WslEnvironmentCheck : IEnvironmentCheck
{
    public int Order => 20; // Fast check - file system reads

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        // WSL detection only relevant on Linux
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Not running on Linux, nothing to check
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
        }

        // Check for WSL-specific environment indicators
        var isWsl = IsRunningInWsl();

        if (!isWsl)
        {
            // Running on native Linux, nothing to check
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
        }

        // Detect WSL version
        var wslVersion = GetWslVersion();

        if (wslVersion == 1)
        {
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "wsl",
                Status = EnvironmentCheckStatus.Warning,
                Message = "WSL1 detected - limited container support",
                Fix = "Upgrade to WSL2 for best experience: wsl --set-version <distro> 2",
                Link = "https://aka.ms/aspire-prerequisites#wsl-setup"
            }]);
        }

        // WSL2 detected - just informational, not a warning unless there are known issues
        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([new EnvironmentCheckResult
        {
            Category = "environment",
            Name = "wsl",
            Status = EnvironmentCheckStatus.Pass,
            Message = "WSL2 environment detected",
            Details = "If you experience container connectivity issues, ensure Docker Desktop WSL integration is enabled."
        }]);
    }

    private static bool IsRunningInWsl()
    {
        try
        {
            // Check for WSL-specific indicators
            if (File.Exists("/proc/version"))
            {
                var version = File.ReadAllText("/proc/version");
                return version.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
                       version.Contains("WSL", StringComparison.OrdinalIgnoreCase);
            }

            // Alternative: check for WSL environment variable
            return Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null;
        }
        catch
        {
            return false;
        }
    }

    private static int GetWslVersion()
    {
        try
        {
            // WSL2 uses a real Linux kernel, WSL1 doesn't
            // Check /proc/version for indicators
            if (File.Exists("/proc/version"))
            {
                var version = File.ReadAllText("/proc/version");
                // WSL2 typically includes "microsoft" and version 4.x or 5.x
                if (version.Contains("microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    // Try to determine version based on kernel version
                    // WSL2 uses kernel 4.x or higher, WSL1 uses much older version strings
                    // Use regex to match actual kernel version numbers (e.g., "Linux version 4.19" or "Linux version 5.10")
                    var kernelVersionMatch = Regex.Match(version, @"Linux\s+version\s+(\d+)\.", RegexOptions.IgnoreCase);
                    if (kernelVersionMatch.Success && int.TryParse(kernelVersionMatch.Groups[1].Value, out int majorVersion))
                    {
                        // WSL2 uses kernel 4.x or higher
                        if (majorVersion >= 4)
                        {
                            return 2;
                        }
                    }
                    return 1;
                }
            }
        }
        catch
        {
            // If we can't determine, assume WSL2 (more common now)
        }

        return 2;
    }
}
