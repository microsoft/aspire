// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Dependency-free helpers for identifying a process by PID plus its start time, used by the
/// various orphan/parent-liveness watchdogs that must survive PID reuse.
/// </summary>
/// <remarks>
/// Lives in the global namespace (like <c>ProcessSignaler</c>) and takes no logging or
/// P/Invoke dependency so it can be linked into assemblies that cannot reference the richer
/// <c>ProcessSignaler</c> (for example <c>Aspire.Hosting.RemoteHost</c> and the
/// <c>aspire-managed</c> host). It is the single source of truth for the "±tolerance start-time
/// match" comparison; <c>ProcessSignaler.AreClose</c> delegates here.
/// </remarks>
internal static class ProcessStartTimeHelper
{
    // Process start times reported by the OS have sub-second precision, while the values we
    // round-trip through environment variables / sockets are second-granularity Unix timestamps.
    // Compare with a one-second tolerance so a truncated expected value still matches.
    private static readonly TimeSpan s_defaultTolerance = TimeSpan.FromSeconds(1);

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
    /// <param name="tolerance">Allowed difference between the expected and observed start time. Defaults to one second.</param>
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
    /// Returns whether two start times, expressed as whole Unix seconds, are within
    /// <paramref name="tolerance"/> (default one second) of each other.
    /// </summary>
    public static bool AreClose(long expectedStartTimeUnixSeconds, long actualStartTimeUnixSeconds, TimeSpan? tolerance = null)
    {
        tolerance ??= s_defaultTolerance;
        return Math.Abs(expectedStartTimeUnixSeconds - actualStartTimeUnixSeconds) <= (long)tolerance.Value.TotalSeconds;
    }
}
