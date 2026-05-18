// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using SystemProcess = System.Diagnostics.Process;

namespace Aspire.Hosting.Dcp;

internal sealed record DcpProcessIdentity(int ProcessId, DateTime Timestamp);

internal static partial class DcpProcessMonitor
{
    private const int DefaultLinuxClockTicksPerSecond = 100;
    private const int LinuxClockTicksPerSecondConfigName = 2; // _SC_CLK_TCK

    internal static DcpProcessIdentity GetMonitorProcessIdentity(SystemProcess parentProcess)
    {
        ArgumentNullException.ThrowIfNull(parentProcess);

        var monitorProcessId = parentProcess.Id;
        var timestamp = GetProcessIdentityTimestamp(parentProcess);

        if (timestamp is null)
        {
            throw new InvalidOperationException($"Could not determine the identity timestamp for monitor process {monitorProcessId}.");
        }

        return new(monitorProcessId, timestamp.Value);
    }

    private static DateTime? GetProcessIdentityTimestamp(SystemProcess parentProcess)
    {
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxProcessIdentityTimestamp(parentProcess.Id);
        }

        try
        {
            return parentProcess.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTime? GetLinuxProcessIdentityTimestamp(int processId)
    {
        var statPath = Path.Combine(
            Environment.GetEnvironmentVariable("HOST_PROC") ?? "/proc",
            processId.ToString(CultureInfo.InvariantCulture),
            "stat");

        string contents;
        try
        {
            contents = File.ReadAllText(statPath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Could not read monitor process stat file '{statPath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Could not read monitor process stat file '{statPath}'.", ex);
        }

        // /proc/<pid>/stat fields start as:
        //   12345 (process name may contain spaces or parentheses) S 1 2 3 ...
        // The process start time is field 22, in clock ticks since boot. Match DCP's
        // Linux identity time by converting that monotonic value into a DateTime
        // offset from DateTime.MinValue instead of estimating a wall-clock time.
        var closeParenIndex = contents.LastIndexOf(')');
        if (closeParenIndex < 0 || closeParenIndex + 2 >= contents.Length)
        {
            throw new InvalidOperationException($"Monitor process stat file '{statPath}' was malformed.");
        }

        var fields = contents[(closeParenIndex + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], CultureInfo.InvariantCulture, out var startTicks))
        {
            throw new InvalidOperationException($"Monitor process stat file '{statPath}' did not contain a valid start time.");
        }

        var startTimeMilliseconds = (startTicks * 1000) / (ulong)GetLinuxClockTicksPerSecond();
        return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc).AddMilliseconds(startTimeMilliseconds);
    }

    private static int GetLinuxClockTicksPerSecond()
    {
        var result = sysconf(LinuxClockTicksPerSecondConfigName);
        return result > 0 ? (int)result : DefaultLinuxClockTicksPerSecond;
    }

    [LibraryImport("libc", SetLastError = true, EntryPoint = "sysconf")]
    private static partial long sysconf(int name);
}
