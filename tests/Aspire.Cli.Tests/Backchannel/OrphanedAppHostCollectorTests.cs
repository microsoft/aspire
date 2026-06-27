// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Backchannel;

public class OrphanedAppHostCollectorTests
{
    [Fact]
    public void IsOrphaned_NoAppHostInfo_ReturnsFalse()
    {
        var connection = new TestAppHostAuxiliaryBackchannel { AppHostInfo = null };

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_NoCliProcessId_ReturnsFalse()
    {
        // Without a known launching CLI we cannot attribute ownership, so never collect.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = null,
            },
        };

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_LiveCliProcess_ReturnsFalse()
    {
        // The current process stands in for a launching CLI that is still alive.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = Environment.ProcessId,
                CliStartedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime),
            },
        };

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_LiveCliProcessWithMismatchedStartTime_ReturnsTrue()
    {
        // The PID is alive but its start time does not match — a recycled PID. The original launcher is
        // gone, so the AppHost is orphaned.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = Environment.ProcessId,
                CliStartedAt = DateTimeOffset.FromUnixTimeSeconds(1),
            },
        };

        Assert.True(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_DeadCliProcess_ReturnsTrue()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t localhost") { CreateNoWindow = true }
            : new ProcessStartInfo("tail", "-f /dev/null") { CreateNoWindow = true };
        using var cliProcess = Process.Start(psi);
        Assert.NotNull(cliProcess);

        var cliStartedAt = new DateTimeOffset(cliProcess.StartTime);
        cliProcess.Kill(entireProcessTree: true);
        cliProcess.WaitForExit();

        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = cliProcess.Id,
                CliStartedAt = cliStartedAt,
            },
        };

        Assert.True(OrphanedAppHostCollector.IsOrphaned(connection));
    }
}
