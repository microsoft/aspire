// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if running in WSL environment and detects potential issues.
/// </summary>
internal sealed partial class WslEnvironmentCheck : IEnvironmentCheck
{
    internal const string CheckName = "wsl";

    private const string ProcVersionPath = "/proc/version";
    private const string WslDistroNameVariable = "WSL_DISTRO_NAME";

    private readonly IEnvironment _environment;
    private readonly Func<string?> _readProcVersion;

    public WslEnvironmentCheck(IEnvironment environment)
        : this(environment, TryReadProcVersion)
    {
    }

    // Lets tests supply a kernel banner without depending on the host actually being WSL.
    internal WslEnvironmentCheck(IEnvironment environment, Func<string?> readProcVersion)
    {
        _environment = environment;
        _readProcVersion = readProcVersion;
    }

    public int Order => 20; // Fast check - file system reads

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        // WSL detection only relevant on Linux
        if (!_environment.IsLinux())
        {
            // Not running on Linux, nothing to check
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
        }

        // Read the kernel banner once and reuse it for both detection and version classification so
        // the two decisions cannot disagree about what the file said.
        var procVersion = _readProcVersion();

        if (!IsRunningInWsl(procVersion))
        {
            // Running on native Linux, nothing to check
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
        }

        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([CreateResult(DetermineWslVersion(procVersion))]);
    }

    /// <summary>
    /// Builds the reported row for a classified WSL environment. An undetermined version reports a
    /// warning rather than a pass: doctor must not assert an environment it was unable to observe.
    /// </summary>
    internal static EnvironmentCheckResult CreateResult(WslVersion wslVersion) => wslVersion switch
    {
        WslVersion.Wsl1 => new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = "WSL1 detected - limited container support",
            Fix = "Upgrade to WSL2 for best experience: wsl --set-version <distro> 2",
            Link = "https://aka.ms/aspire-prerequisites#wsl-setup"
        },
        WslVersion.Wsl2 => new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Pass,
            Message = "WSL2 environment detected",
            Details = "If you experience container connectivity issues, ensure Docker Desktop WSL integration is enabled."
        },
        _ => new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = "WSL detected but the version could not be determined",
            Details = $"Could not classify the WSL version from {ProcVersionPath}. WSL1 has limited container support, so this environment may not be able to run containers.",
            Fix = "Run 'wsl --list --verbose' from Windows to check the version, then upgrade with: wsl --set-version <distro> 2",
            Link = "https://aka.ms/aspire-prerequisites#wsl-setup"
        }
    };

    /// <summary>
    /// Determines whether the current Linux environment is WSL, using the kernel banner when it is
    /// readable and falling back to the variable WSL injects into every distribution shell.
    /// </summary>
    private bool IsRunningInWsl(string? procVersion)
    {
        if (procVersion is not null &&
            (procVersion.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
             procVersion.Contains("WSL", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(_environment.GetEnvironmentVariable(WslDistroNameVariable));
    }

    /// <summary>
    /// Classifies the WSL version from the contents of <c>/proc/version</c>, returning
    /// <see cref="WslVersion.Unknown"/> when the contents do not identify a version.
    /// </summary>
    internal static WslVersion DetermineWslVersion(string? procVersion)
    {
        if (string.IsNullOrWhiteSpace(procVersion))
        {
            return WslVersion.Unknown;
        }

        // WSL 2 runs a genuine Microsoft-built kernel whose release string carries a "WSL2" marker:
        //   Linux version 5.15.90.1-microsoft-standard-WSL2 (oe-user@oe-host) (...) #1 SMP ...
        if (procVersion.Contains("WSL2", StringComparison.OrdinalIgnoreCase))
        {
            return WslVersion.Wsl2;
        }

        // WSL 1 has no real kernel. It reports a fixed 4.4.0 compatibility banner with a "-Microsoft"
        // suffix, kept only so tools that parse a kernel version keep working:
        //   Linux version 4.4.0-19041-Microsoft (Microsoft@Microsoft.com) (gcc version 5.4.0 (GCC) ) ...
        // Because 4.4.0 is a constant rather than a real release, it must not be compared ordinally
        // against WSL 2 kernels; early WSL 2 shipped 4.19, so any "major >= 4" test classifies real
        // WSL 1 systems as WSL 2. See https://learn.microsoft.com/windows/wsl/compare-versions
        if (procVersion.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            Wsl1KernelBanner().IsMatch(procVersion))
        {
            return WslVersion.Wsl1;
        }

        // A custom WSL 2 kernel configured through .wslconfig can omit both markers. Report the
        // version as undetermined instead of guessing, because either guess is confidently wrong:
        // claiming WSL 2 hides the container limitations this check exists to surface, and claiming
        // WSL 1 tells a WSL 2 user to perform an upgrade they do not need.
        return WslVersion.Unknown;
    }

    private static string? TryReadProcVersion()
    {
        try
        {
            return File.Exists(ProcVersionPath) ? File.ReadAllText(ProcVersionPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable banner must degrade to "unknown" so the caller reports a warning rather
            // than silently assuming a version.
            return null;
        }
    }

    [GeneratedRegex(@"Linux\s+version\s+4\.4\.", RegexOptions.IgnoreCase)]
    private static partial Regex Wsl1KernelBanner();
}

/// <summary>
/// The WSL version detected for the current environment.
/// </summary>
internal enum WslVersion
{
    /// <summary>WSL 1, which emulates Linux system calls and has limited container support.</summary>
    Wsl1,

    /// <summary>WSL 2, which runs a real Linux kernel in a lightweight VM.</summary>
    Wsl2,

    /// <summary>WSL was detected but the version could not be determined.</summary>
    Unknown
}
