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
            logger.LogDebug("Windows graceful process shutdown is handled by caller-specific process tree signaling.");
        }
        else
        {
            RequestGracefulShutdownUnix(pid, logger);
        }
    }

    public static void RequestGracefulShutdownForProcessGroup(int pid, DateTimeOffset? expectedStartTime, ILogger logger)
    {
        using var process = TryGetRunningProcess(pid, expectedStartTime, logger);
        if (process is null)
        {
            return; // Process is not running or does not match the expected start time
        }

        logger.LogDebug("Requesting graceful shutdown of process group {Pid}...", pid);

        if (OperatingSystem.IsWindows())
        {
            RequestGracefulShutdownWindowsProcessGroup(pid, logger);
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
    // Use CTRL_BREAK_EVENT for Windows process-group shutdown. Ctrl+C is not suitable here because
    // Windows disables Ctrl+C delivery for processes launched with CREATE_NEW_PROCESS_GROUP.
    // See https://learn.microsoft.com/windows/console/generateconsolectrlevent.
    private const uint CtrlBreakEvent = 1;

    private static void RequestGracefulShutdownWindowsProcessGroup(int pid, ILogger logger)
    {
        var result = GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)pid);
        if (!result)
        {
            var error = Marshal.GetLastWin32Error();
            // Best effort.
            logger.LogWarning("Could not gracefully stop Aspire application host process group {Pid}; the error code from signal send operation was {ErrorCode}", pid, error);
        }
    }

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
