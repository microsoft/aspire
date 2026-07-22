// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provides best-effort process signaling for graceful shutdown and forceful termination.
/// </summary>
internal static partial class ProcessSignaler
{
    public static void RequestGracefulShutdown(int pid, DateTimeOffset? expectedStartTime, ILogger logger)
    {
        using var process = TryGetRunningProcess(pid, expectedStartTime, logger);
        if (process is null)
        {
            return; // Process is not running or does not match the expected start time
        }

        RequestGracefulShutdown(pid, logger);
    }

    public static void RequestGracefulShutdownWithRuntimeStartTime(int pid, DateTimeOffset expectedStartTime, TimeSpan tolerance, ILogger logger)
    {
        using var process = TryGetRunningProcessWithRuntimeStartTime(pid, expectedStartTime, tolerance, logger);
        if (process is null)
        {
            return; // Process is not running or does not match the expected start time
        }

        RequestGracefulShutdown(pid, logger);
    }

    private static void RequestGracefulShutdown(int pid, ILogger logger)
    {
        logger.LogDebug("Requesting graceful shutdown of process {Pid}...", pid);

        if (OperatingSystem.IsWindows())
        {
            logger.LogDebug("Windows graceful process shutdown is handled by caller-specific process tree signaling.");
        }
        else
        {
            RequestGracefulShutdownUnix(pid, logger);
        }
    }

    public static void ForceKill(int pid, DateTimeOffset? expectedStartTime, ILogger logger, bool killEntireProcessTree = false)
    {
        using var process = TryGetRunningProcess(pid, expectedStartTime, logger);
        if (process is { })
        {
            logger.LogDebug("Killing process {Pid} (entireProcessTree={EntireProcessTree})...", pid, killEntireProcessTree);
            try
            {
                process.Kill(entireProcessTree: killEntireProcessTree);
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
        }
    }

    public static Process? TryGetRunningProcess(int pid, DateTimeOffset? expectedStartTime, ILogger logger)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            if (expectedStartTime is not null)
            {
                var actualStartTimeUnixMilliseconds = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(pid);
                if (actualStartTimeUnixMilliseconds is null)
                {
                    logger.LogDebug("Could not inspect process {Pid} start time. Treating it as not running.", pid);
                    process.Dispose();
                    return null;
                }

                var expectedStartTimeUnixMilliseconds = expectedStartTime.Value.ToUnixTimeMilliseconds();
                if (!ProcessStartTimeHelper.AreCloseMilliseconds(expectedStartTimeUnixMilliseconds, actualStartTimeUnixMilliseconds.Value))
                {
                    logger.LogDebug("Process {Pid} start time {ProcessStartTimeMs}ms does not match expected start time {ExpectedStartTimeMs}ms", pid, actualStartTimeUnixMilliseconds, expectedStartTimeUnixMilliseconds);
                    process.Dispose();
                    return null; // Do not return processes that do not match the expected start time
                }

                if (process.HasExited)
                {
                    process.Dispose();
                    return null;
                }
            }

            return process;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist - already terminated.
            process?.Dispose();
            return null;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
            process?.Dispose();
            return null;
        }
        catch (Win32Exception ex)
        {
            // Process inspection can race with process exit. On macOS, StartTime can throw:
            //   Win32Exception (3): Unable to retrieve the specified information about the process or thread. It may have exited or may be privileged.
            // If we cannot inspect the process enough to prove it is the expected target, do
            // not signal or kill it.
            logger.LogDebug(ex, "Could not inspect process {Pid}. Treating it as not running.", pid);
            process?.Dispose();
            return null;
        }
    }

    public static Process? TryGetRunningProcessWithRuntimeStartTime(int pid, DateTimeOffset expectedStartTime, TimeSpan tolerance, ILogger logger)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            var expectedStartTimeUnix = expectedStartTime.ToUnixTimeSeconds();
            if (!ProcessStartTimeHelper.IsProcessRunningWithRuntimeStartTime(pid, expectedStartTimeUnix, tolerance))
            {
                logger.LogDebug("Process {Pid} legacy start time does not match expected start time {ExpectedStartTime}.", pid, expectedStartTime);
                process.Dispose();
                return null;
            }

            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist - already terminated.
            process?.Dispose();
            return null;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
            process?.Dispose();
            return null;
        }
        catch (Win32Exception ex)
        {
            // Process inspection can race with process exit. On macOS, StartTime can throw:
            //   Win32Exception (3): Unable to retrieve the specified information about the process or thread. It may have exited or may be privileged.
            // If we cannot inspect the process enough to prove it is the expected target, do
            // not signal or kill it.
            logger.LogDebug(ex, "Could not inspect process {Pid}. Treating it as not running.", pid);
            process?.Dispose();
            return null;
        }
    }

    private const int SigTerm = 15;

    private static void RequestGracefulShutdownUnix(int pid, ILogger logger)
    {
        var result = kill(pid, SigTerm);
        if (result != 0)
        {
            int errno = Marshal.GetLastSystemError();
            // Best effort.
            logger.LogWarning("Could not gracefully stop Aspire application host process {Pid}; the error code from signal send operation was {ErrorCode}", pid, errno);
        }
    }

    // "libc" here is a moniker for standard C library, which .NET maps to system C library on Unix-like systems.
    // See https://developers.redhat.com/blog/2019/03/25/using-net-pinvoke-for-linux-system-functions
    [LibraryImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static partial int kill(int pid, int sig);
}
