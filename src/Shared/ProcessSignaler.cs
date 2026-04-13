// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

        logger.LogDebug("Requesting graceful shutdown of process {Pid}...", pid);

        if (OperatingSystem.IsWindows())
        {
            RequestGracefulShutdownWindows(pid, logger);
        }
        else
        {
            RequestGracefulShutdownUnix(pid, logger);
        }
    }

    public static void ForceKill(int pid, DateTimeOffset? expectedStartTime, ILogger logger)
    {
        using var process = TryGetRunningProcess(pid, expectedStartTime, logger);
        if (process is { })
        {
            logger.LogDebug("Killing process {Pid}...", pid);
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch(InvalidOperationException)
            {
                // Process already exited.
            }
        }
    }

    public static Process? TryGetRunningProcess(int pid, DateTimeOffset? expectedStartTime, ILogger logger)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (expectedStartTime is not null && !AreClose(expectedStartTime, process.StartTime))
            {
                logger.LogDebug("Process {Pid} start time {ProcessStartTime} does not match expected start time {ExpectedStartTime}", pid, process.StartTime, expectedStartTime);
                process.Dispose();
                return null; // Do not return processes that do not match the expected start time
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
            return null;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
            return null;
        }
    }

    private static bool AreClose(DateTimeOffset? expectedStartTime, DateTime processStartTime, TimeSpan? tolerance = default)
    {
        if (expectedStartTime is null)
        {
            return true;
        }

        tolerance ??= TimeSpan.FromSeconds(1);
        return ((DateTimeOffset)expectedStartTime - new DateTimeOffset(processStartTime)).Duration() <= tolerance;
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

    private const uint CtrlBreakEvent = 1;

    private static void RequestGracefulShutdownWindows(int pid, ILogger logger)
    {
        var success = GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)pid);
        if (!success)
        {
            // Best effort.
            logger.LogWarning("Could not gracefully stop Aspire application host process {Pid}", pid);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    // "libc" here is a moniker for standard C library, which .NET maps to system C library on Unix-like systems.
    // See https://developers.redhat.com/blog/2019/03/25/using-net-pinvoke-for-linux-system-functions
    [LibraryImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static partial int kill(int pid, int sig);
}
