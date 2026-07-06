// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Dependency-free helpers for identifying a process by PID plus its start time, used by the
/// various orphan/parent-liveness watchdogs that must survive PID reuse.
/// </summary>
internal static partial class ProcessStartTimeHelper
{
    internal const string LinuxProcRoot = "/proc";

    private const int DefaultLinuxClockTicksPerSecond = 100;
    private const int LinuxClockTicksPerSecondConfigName = 2; // _SC_CLK_TCK

    /// <summary>
    /// The comparison tolerance cross-process callers must use when comparing process identity time. 
    /// </summary>
    public static readonly TimeSpan CrossProcessIdentityTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the current process's start time as whole Unix seconds. This is the value that should be
    /// propagated to child processes so they can verify the parent's identity (PID + start time).
    /// </summary>
    public static long GetCurrentProcessStartTimeUnixSeconds()
        => GetCurrentProcessStartTime().ToUnixTimeSeconds();

    /// <summary>
    /// Gets the current process's start time as reported by <see cref="Process.StartTime"/>.
    /// </summary>
    public static long GetCurrentProcessRuntimeStartTimeUnixSeconds()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Gets the current process's start time using the same platform-specific source as
    /// <see cref="TryGetProcessStartTime"/>.
    /// </summary>
    public static DateTimeOffset GetCurrentProcessStartTime()
    {
        if (TryGetProcessStartTime(Environment.ProcessId) is { } startTime)
        {
            return startTime;
        }

        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime);
    }

    /// <summary>
    /// Gets the start time, as whole Unix seconds, of the process with the given <paramref name="pid"/>.
    /// </summary>
    /// <returns>The start time, or <see langword="null"/> when the process cannot be inspected (already exited, privileged, etc.).</returns>
    public static long? TryGetProcessStartTimeUnixSeconds(int pid)
        => TryGetProcessStartTime(pid)?.ToUnixTimeSeconds();

    /// <summary>
    /// Gets the start time, as whole Unix seconds, of the process with the given <paramref name="pid"/>
    /// using <see cref="Process.StartTime"/>.
    /// </summary>
    /// <returns>The start time, or <see langword="null"/> when the process cannot be inspected (already exited, privileged, etc.).</returns>
    public static long? TryGetRuntimeProcessStartTimeUnixSeconds(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime).ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the start time of the process with the given <paramref name="pid"/>.
    /// </summary>
    /// <returns>The start time, or <see langword="null"/> when the process cannot be inspected (already exited, privileged, etc.).</returns>
    public static DateTimeOffset? TryGetProcessStartTime(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxProcessStartTime(pid);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a Unix-seconds start time previously produced by
    /// <see cref="GetCurrentProcessStartTimeUnixSeconds"/> (for example from an environment variable).
    /// </summary>
    /// <returns>The parsed value, or <see langword="null"/> when <paramref name="value"/> is missing or invalid.</returns>
    public static long? TryParseStartTimeUnixSeconds(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// Determines whether a process with the given <paramref name="pid"/> is currently running and,
    /// when <paramref name="expectedStartTimeUnixSeconds"/> is supplied, whether its start time matches
    /// (guarding against PID reuse). When the expected start time is <see langword="null"/> this falls
    /// back to a PID-only existence check.
    /// </summary>
    /// <param name="pid">The process ID to check.</param>
    /// <param name="expectedStartTimeUnixSeconds">The expected start time (whole Unix seconds), or <see langword="null"/> for PID-only.</param>
    /// <param name="tolerance">Allowed difference between the expected and observed start time. Defaults to an exact match.</param>
    /// <returns><see langword="true"/> if the process exists and matches; otherwise <see langword="false"/>.</returns>
    public static bool IsProcessRunning(int pid, long? expectedStartTimeUnixSeconds = null, TimeSpan? tolerance = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (expectedStartTimeUnixSeconds is { } expected)
            {
                // Reading the process start time can race with process exit. On macOS it can throw:
                //   Win32Exception (3): Unable to retrieve the specified information about the process or thread.
                // If we cannot prove this is the expected target, treat it as not running so callers
                // never act on a recycled PID.
                var actual = TryGetProcessStartTimeUnixSeconds(pid);
                if (actual is null)
                {
                    return false;
                }

                if (!AreClose(expected, actual.Value, tolerance))
                {
                    return false;
                }

                if (process.HasExited)
                {
                    return false;
                }
            }

            return true;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist - already terminated.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
            return false;
        }
        catch (Win32Exception)
        {
            // Could not inspect the process (e.g. it exited mid-check, or is privileged). Without
            // proof of identity, do not report it as the expected running process.
            return false;
        }
    }

    /// <summary>
    /// Determines whether a process matches a legacy start time that was produced from
    /// <see cref="Process.StartTime"/>. Cross-process callers should pass a one-second
    /// <paramref name="tolerance"/> because on Linux <see cref="Process.StartTime"/> is reconstructed from
    /// an independently sampled boot time and can differ by CrossProcessIdentityTimeTolerance 
    /// between processes reading the same PID.
    /// </summary>
    /// <param name="pid">The process ID to check.</param>
    /// <param name="expectedStartTimeUnixSeconds">The expected legacy start time (whole Unix seconds).</param>
    /// <param name="tolerance">
    /// Allowed difference between the expected and observed start time. Defaults to an exact match; the
    /// cross-process orphan/liveness callers pass <see cref="CrossProcessIdentityTimeTolerance"/> to absorb
    /// the boot-time jitter described above.
    /// </param>
    /// <returns><see langword="true"/> if the process exists and its legacy start time matches; otherwise <see langword="false"/>.</returns>
    public static bool IsProcessRunningWithRuntimeStartTime(int pid, long expectedStartTimeUnixSeconds, TimeSpan? tolerance = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (TryGetRuntimeProcessStartTimeUnixSeconds(pid) is not { } actual ||
                !AreClose(expectedStartTimeUnixSeconds, actual, tolerance))
            {
                return false;
            }

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist - already terminated.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
            return false;
        }
        catch (Win32Exception)
        {
            // Could not inspect the process (e.g. it exited mid-check, or is privileged). Without
            // proof of identity, do not report it as the expected running process.
            return false;
        }
    }

    /// <summary>
    /// Returns whether two start times, expressed as whole Unix seconds, identify the same process.
    /// The comparison is exact by default: both values are already truncated to whole seconds, so
    /// accepting an adjacent second would let a PID recycled into the neighboring second impersonate the
    /// original process and defeat the reuse guard.
    /// </summary>
    /// <param name="expectedStartTimeUnixSeconds">The expected start time, in whole Unix seconds.</param>
    /// <param name="actualStartTimeUnixSeconds">The observed start time, in whole Unix seconds.</param>
    /// <param name="tolerance">
    /// Optional allowed difference between the two values. Defaults to an exact match. Callers should only
    /// opt into a non-zero tolerance when they must absorb cross-process jitter in the OS-reported start time. 
    /// </param>
    public static bool AreClose(long expectedStartTimeUnixSeconds, long actualStartTimeUnixSeconds, TimeSpan? tolerance = null)
    {
        var toleranceSeconds = tolerance is { } value ? (long)value.TotalSeconds : 0;
        return Math.Abs(expectedStartTimeUnixSeconds - actualStartTimeUnixSeconds) <= toleranceSeconds;
    }

    private static DateTimeOffset? TryGetLinuxProcessStartTime(int pid)
    {
        // These identities are always stamped with PIDs from the current process namespace.
        // Do not honor HOST_PROC here: that is only for DCP host-process inspection, and
        // mixing local PIDs with host /proc data can make orphan detection act on the wrong process.
        if (TryGetLinuxProcessStartTicks(pid, LinuxProcRoot) is not { } startTicks)
        {
            return null;
        }

        // Boot-relative seconds only. startTicks is field 22 of /proc/<pid>/stat, fixed at process start
        // relative to boot, so this is identical for every reader regardless of any later wall-clock step.
        // We intentionally do NOT add /proc/stat btime: btime re-expresses this as wall-clock time and
        // itself shifts on clock sync, which would re-introduce the drift this guards against. The result
        // is an opaque same-machine identity token (seconds since boot), not a real calendar time. This
        // mirrors DcpProcessMonitor.GetLinuxProcessIdentityTimestamp so every Aspire watchdog computes the
        // same value for a given PID.
        var startTimeSecondsSinceBoot = (long)(startTicks / (ulong)GetLinuxClockTicksPerSecond());
        return DateTimeOffset.FromUnixTimeSeconds(startTimeSecondsSinceBoot);
    }

    /// <summary>
    /// Reads the raw start time of the process with the given <paramref name="pid"/> as clock ticks since
    /// boot (field 22 of <c>/proc/&lt;pid&gt;/stat</c>). This is the drift-immune building block for Linux
    /// process identity: the value is fixed at process start relative to boot, so every reader on the
    /// machine observes the same number even after a wall-clock adjustment. 
    /// Divide by <see cref="GetLinuxClockTicksPerSecond"/> to obtain seconds since boot.
    /// </summary>
    /// <remarks>
    /// Using this is especially important for containers, where suspension and resumption of the host can cause
    /// the wall clock to jump. 
    /// </remarks>
    /// <param name="pid">The process to inspect.</param>
    /// <param name="procRoot"> The <c>/proc</c> filesystem root to read from. 
    /// Normally /proc, but can be overridden for container environments to read the host process namespace.
    /// </param>
    /// <returns>The start ticks, or <see langword="null"/> when the stat file cannot be read or parsed.</returns>
    internal static ulong? TryGetLinuxProcessStartTicks(int pid, string procRoot)
    {
        var statPath = Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), "stat");

        string contents;
        try
        {
            contents = File.ReadAllText(statPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return TryParseLinuxStatStartTicks(contents);
    }

    internal static ulong? TryParseLinuxStatStartTicks(string contents)
    {
        // /proc/<pid>/stat fields start as:
        //   12345 (process name may contain spaces or parentheses) S 1 2 3 ...
        // The process start time is field 22, in clock ticks since boot. Split after the final
        // ')' so process names containing spaces or parentheses do not shift the field indexes.
        var closeParenIndex = contents.LastIndexOf(')');
        if (closeParenIndex < 0 || closeParenIndex + 2 >= contents.Length)
        {
            return null;
        }

        var fields = contents[(closeParenIndex + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 20 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks)
            ? startTicks
            : null;
    }

    /// <summary>
    /// Gets the Linux clock resolution (<c>_SC_CLK_TCK</c>) used to convert <c>/proc</c> start ticks to
    /// seconds. Shared so every Linux process-identity computation (including <c>DcpProcessMonitor</c>) uses
    /// the same value.
    /// </summary>
    internal static int GetLinuxClockTicksPerSecond()
    {
        var result = sysconf(LinuxClockTicksPerSecondConfigName);
        return result > 0 ? (int)result : DefaultLinuxClockTicksPerSecond;
    }

    [LibraryImport("libc", SetLastError = true, EntryPoint = "sysconf")]
    private static partial long sysconf(int name);
}
