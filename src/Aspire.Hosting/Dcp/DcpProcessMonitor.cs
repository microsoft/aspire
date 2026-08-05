// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SystemProcess = System.Diagnostics.Process;

namespace Aspire.Hosting.Dcp;

internal sealed record DcpProcessIdentity(int ProcessId, DateTime Timestamp);

internal static class DcpProcessMonitor
{
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

    private static DateTime GetLinuxProcessIdentityTimestamp(int processId)
    {
        // DCP inspects the *host* process table, so honor HOST_PROC when this check runs inside a
        // container whose host /proc is mounted elsewhere. Orphan/liveness detection deliberately uses the
        // current namespace's /proc instead (see ProcessStartTimeHelper.TryGetProcessStartTime), which is
        // why the /proc root is a parameter of the shared reader rather than baked into it.
        var procRoot = Environment.GetEnvironmentVariable("HOST_PROC") ?? "/proc";

        // Share the /proc start-ticks reader with every other Aspire watchdog so there is one Linux
        // process-identity implementation. The value is boot-relative (field 22 of /proc/<pid>/stat),
        // which is why it is immune to wall-clock drift.
        if (ProcessStartTimeHelper.TryGetLinuxProcessStartTicks(processId, procRoot) is not { } startTicks)
        {
            throw new InvalidOperationException($"Could not read a valid start time for monitor process {processId} from '{procRoot}'.");
        }

        // Convert the boot-relative start ticks into a DateTime offset from DateTime.MinValue instead of
        // estimating a wall-clock time. Kept in milliseconds for parity with the timestamp DCP compares
        // against, which is a distinct identity domain from the whole-second value used by the orphan
        // detectors, so the two never cross-compare.
        var startTimeMilliseconds = (startTicks * 1000) / (ulong)ProcessStartTimeHelper.GetLinuxClockTicksPerSecond();
        return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc).AddMilliseconds(startTimeMilliseconds);
    }
}
