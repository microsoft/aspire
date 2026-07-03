// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Hosting.Tests;

public class ProcessStartTimeHelperTests
{
    [Fact]
    public void IsProcessRunning_CurrentProcess_ReturnsTrue()
    {
        Assert.True(ProcessStartTimeHelper.IsProcessRunning(Environment.ProcessId));
    }

    [Fact]
    public void IsProcessRunning_CurrentProcessWithMatchingStartTime_ReturnsTrue()
    {
        var startedUnix = ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds();
        Assert.True(ProcessStartTimeHelper.IsProcessRunning(Environment.ProcessId, startedUnix));
    }

    [Fact]
    public void IsProcessRunning_CurrentProcessWithWrongStartTime_ReturnsFalse()
    {
        // A start time decades off can never match, simulating PID reuse.
        Assert.False(ProcessStartTimeHelper.IsProcessRunning(Environment.ProcessId, expectedStartTimeUnixSeconds: 1));
    }

    [Fact]
    public void IsProcessRunning_TracksRealProcessLifetime()
    {
        // Mirrors CliOrphanDetectorTests' process choice for cross-platform reliability in CI.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t localhost") { CreateNoWindow = true }
            : new ProcessStartInfo("tail", "-f /dev/null") { CreateNoWindow = true };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var pid = process.Id;
        var startedUnix = ProcessStartTimeHelper.TryGetProcessStartTimeUnixSeconds(process.Id);
        Assert.NotNull(startedUnix);

        Assert.True(ProcessStartTimeHelper.IsProcessRunning(pid));
        Assert.True(ProcessStartTimeHelper.IsProcessRunning(pid, startedUnix.Value));

        process.Kill(entireProcessTree: true);
        process.WaitForExit();

        Assert.False(ProcessStartTimeHelper.IsProcessRunning(pid));
        Assert.False(ProcessStartTimeHelper.IsProcessRunning(pid, startedUnix.Value));
    }

    [Fact]
    public void GetCurrentProcessStartTimeUnixSeconds_ReturnsPositiveValue()
    {
        Assert.True(ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds() > 0);
    }

    [Fact]
    public void TryGetProcessStartTimeUnixSeconds_CurrentProcess_MatchesCurrentValue()
    {
        var fromPid = ProcessStartTimeHelper.TryGetProcessStartTimeUnixSeconds(Environment.ProcessId);
        Assert.NotNull(fromPid);
        Assert.Equal(ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds(), fromPid);
    }

    [Theory]
    [InlineData("dotnet", 123456UL)]
    [InlineData("process name with ) parenthesis", 987654UL)]
    public void TryParseLinuxStatStartTicks_ParsesField22AfterProcessName(string processName, ulong expectedStartTicks)
    {
        var fields = Enumerable.Range(0, 20).Select(static i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        fields[0] = "S";
        fields[19] = expectedStartTicks.ToString(CultureInfo.InvariantCulture);
        var stat = $"12345 ({processName}) {string.Join(' ', fields)}";

        Assert.Equal(expectedStartTicks, ProcessStartTimeHelper.TryParseLinuxStatStartTicks(stat));
    }

    [Fact]
    public void LinuxProcRoot_UsesCurrentProcessNamespace()
    {
        Assert.Equal("/proc", ProcessStartTimeHelper.LinuxProcRoot);
    }

    [Theory]
    [InlineData("12345 missing-close-paren S 1 2 3")]
    [InlineData("12345 (dotnet) S 1 2 3")]
    public void TryParseLinuxStatStartTicks_Malformed_ReturnsNull(string stat)
    {
        Assert.Null(ProcessStartTimeHelper.TryParseLinuxStatStartTicks(stat));
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("0", 0L)]
    [InlineData(" 456 ", 456L)]
    public void TryParseStartTimeUnixSeconds_Valid_ReturnsValue(string value, long expected)
    {
        Assert.Equal(expected, ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void TryParseStartTimeUnixSeconds_Invalid_ReturnsNull(string? value)
    {
        Assert.Null(ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(value));
    }

    [Theory]
    [InlineData(1000L, 1000L, true)]   // exact match
    [InlineData(1000L, 1001L, false)]  // adjacent second: a recycled PID must not match
    [InlineData(1000L, 999L, false)]   // adjacent second: a recycled PID must not match
    [InlineData(1000L, 1002L, false)]  // clearly different
    public void AreClose_DefaultsToExactMatch(long expected, long actual, bool expectedResult)
    {
        Assert.Equal(expectedResult, ProcessStartTimeHelper.AreClose(expected, actual));
    }

    [Theory]
    [InlineData(1000L, 1001L, true)]   // within the opt-in one-second tolerance
    [InlineData(1000L, 999L, true)]    // within the opt-in one-second tolerance
    [InlineData(1000L, 1002L, false)]  // outside the opt-in one-second tolerance
    public void AreClose_WithOptInTolerance_AllowsNeighboringSeconds(long expected, long actual, bool expectedResult)
    {
        Assert.Equal(expectedResult, ProcessStartTimeHelper.AreClose(expected, actual, TimeSpan.FromSeconds(1)));
    }
}
