// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

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
        var startedUnix = ((DateTimeOffset)process.StartTime).ToUnixTimeSeconds();

        Assert.True(ProcessStartTimeHelper.IsProcessRunning(pid));
        Assert.True(ProcessStartTimeHelper.IsProcessRunning(pid, startedUnix));

        process.Kill(entireProcessTree: true);
        process.WaitForExit();

        Assert.False(ProcessStartTimeHelper.IsProcessRunning(pid));
        Assert.False(ProcessStartTimeHelper.IsProcessRunning(pid, startedUnix));
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
