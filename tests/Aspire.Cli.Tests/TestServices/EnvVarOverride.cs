// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Restores an environment variable to its prior value on dispose. Used
/// in DiscoverAll tests to point the discovery walk at a controlled
/// <c>HOME</c> / <c>USERPROFILE</c> / <c>PATH</c> sandbox.
/// </summary>
internal sealed class EnvVarOverride : IDisposable
{
    /// <summary>
    /// The launcher-watchdog poll interval used by tests that wait for a killed launcher to be detected.
    /// The production default is one second; shortening it keeps those tests about the watchdog firing
    /// rather than about wall-clock scheduling under CPU contention.
    /// </summary>
    public static readonly TimeSpan LauncherWatchdogTestPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly string _name;
    private readonly string? _previous;

    public EnvVarOverride(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Sets the variable to the PID of a process that has already exited, so code watching that
    /// process observes the parent as gone immediately.
    /// </summary>
    public static EnvVarOverride CreateForExitedProcess(string name)
    {
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            ArgumentList = { "--info" },
            // Not redirected: this file is under test, so inheriting the test host's pipes would keep
            // WriteLineAsync waiting on a pipe the exited child no longer reads.
            UseShellExecute = false,
        })!;
        exited.WaitForExit();

        return new EnvVarOverride(name, exited.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Sets the variable to the PID of a process that is still running, so a test can arm a watchdog
    /// against a live parent and then kill it mid-stream. The caller owns <paramref name="parent"/> and
    /// is responsible for killing and disposing it; the returned override only restores the variable.
    /// </summary>
    public static EnvVarOverride CreateForRunningProcess(string name, System.Diagnostics.Process parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new EnvVarOverride(name, parent.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets a start time, in the legacy whole-Unix-seconds domain the CLI parent watchdog reads from
    /// <c>ASPIRE_CLI_PARENT_STARTED</c>, that does not match <paramref name="parent"/>'s real start time.
    /// A watchdog checking PID plus start time must then treat the live process as a recycled PID.
    /// </summary>
    public static string CreateMismatchedStartTimeUnixSeconds(System.Diagnostics.Process parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var actual = ProcessStartTimeHelper.TryGetRuntimeProcessStartTimeUnixSeconds(parent.Id)
            ?? throw new InvalidOperationException($"Could not read the start time of process {parent.Id}.");
        return (actual - LauncherStartTimeMismatchSeconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How far the mismatched start time sits from the real one. Must exceed
    /// <see cref="ProcessStartTimeHelper.LegacyStartTimeMatchTolerance"/> so the mismatch is never
    /// absorbed as boot-time jitter.
    /// </summary>
    public static readonly long LauncherStartTimeMismatchSeconds =
        (long)ProcessStartTimeHelper.LegacyStartTimeMatchTolerance.TotalSeconds * 10;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previous);
    }
}
