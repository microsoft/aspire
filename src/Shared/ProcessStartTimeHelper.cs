// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Dependency-free helpers for identifying a process by PID plus its start time, used by the
/// various orphan/parent-liveness watchdogs that must survive PID reuse.
/// </summary>
internal static class ProcessStartTimeHelper
{
    /// <summary>
    /// Gets the current process's start time as whole Unix seconds. This is the value that should be
    /// propagated to child processes so they can verify the parent's identity (PID + start time).
    /// </summary>
    public static long GetCurrentProcessStartTimeUnixSeconds()
    {
        using var process = Process.GetCurrentProcess();
        return ((DateTimeOffset)process.StartTime).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Gets the start time, as whole Unix seconds, of the process with the given <paramref name="pid"/>.
    /// </summary>
    /// <returns>The start time, or <see langword="null"/> when the process cannot be inspected (already exited, privileged, etc.).</returns>
    public static long? TryGetProcessStartTimeUnixSeconds(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return ((DateTimeOffset)process.StartTime).ToUnixTimeSeconds();
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
                // Reading StartTime can race with process exit. On macOS it can throw:
                //   Win32Exception (3): Unable to retrieve the specified information about the process or thread.
                // If we cannot prove this is the expected target, treat it as not running so callers
                // never act on a recycled PID.
                var actual = new DateTimeOffset(process.StartTime).ToUnixTimeSeconds();
                if (!AreClose(expected, actual, tolerance))
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
    /// Returns whether two start times, expressed as whole Unix seconds, identify the same process.
    /// The comparison is exact by default: both values are already truncated to whole seconds, so
    /// accepting an adjacent second would let a PID recycled into the neighboring second impersonate the
    /// original process and defeat the reuse guard.
    /// </summary>
    /// <param name="expectedStartTimeUnixSeconds">The expected start time, in whole Unix seconds.</param>
    /// <param name="actualStartTimeUnixSeconds">The observed start time, in whole Unix seconds.</param>
    /// <param name="tolerance">
    /// Optional allowed difference between the two values. Defaults to an exact match. Callers should only
    /// opt into a non-zero tolerance when they must absorb cross-process jitter in the OS-reported start
    /// time. For example, on Linux <see cref="Process.StartTime"/> is derived from an independently sampled
    /// boot time (a coarse clock), so reading the same process from two different processes can truncate to
    /// adjacent seconds near a second boundary. No caller needs this today.
    /// </param>
    public static bool AreClose(long expectedStartTimeUnixSeconds, long actualStartTimeUnixSeconds, TimeSpan? tolerance = null)
    {
        var toleranceSeconds = tolerance is { } value ? (long)value.TotalSeconds : 0;
        return Math.Abs(expectedStartTimeUnixSeconds - actualStartTimeUnixSeconds) <= toleranceSeconds;
    }
}
