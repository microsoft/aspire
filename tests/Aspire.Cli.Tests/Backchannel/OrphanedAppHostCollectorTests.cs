// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task CollectAsync_ScansThenStopsOnlyOrphans()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-orphan-collect-tests");
        try
        {
            var monitor = new TestAuxiliaryBackchannelMonitor();
            var stopper = new TestAppHostStopper();

            var orphanSocket = CreateSocketFile(tempDir, "orphan.sock");
            var liveSocket = CreateSocketFile(tempDir, "live.sock");
            monitor.AddConnection("orphan-hash", orphanSocket, CreateConnection(orphanSocket, appHostPid: 4242, orphaned: true));
            monitor.AddConnection("live-hash", liveSocket, CreateConnection(liveSocket, appHostPid: 4343, orphaned: false));

            var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

            var collected = await collector.CollectAsync(CancellationToken.None);

            Assert.Equal(1, collected);
            // The collector must scan for fresh state before deciding what to collect.
            Assert.Equal(1, monitor.ScanCallCount);
            // Only the orphaned AppHost is stopped; the live one is left running.
            var stopped = Assert.Single(stopper.StopRequests);
            Assert.Equal(4242, stopped?.ProcessId);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CollectAsync_SuccessfulStop_DeletesSocketAndCounts()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-orphan-collect-tests");
        try
        {
            var monitor = new TestAuxiliaryBackchannelMonitor();
            var stopper = new TestAppHostStopper { DefaultResult = true };

            var socketPath = CreateSocketFile(tempDir, "orphan.sock");
            monitor.AddConnection("orphan-hash", socketPath, CreateConnection(socketPath, appHostPid: 4242, orphaned: true));

            var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

            var collected = await collector.CollectAsync(CancellationToken.None);

            Assert.Equal(1, collected);
            // A confirmed stop must remove the now-dead AppHost's stale socket.
            Assert.False(File.Exists(socketPath));
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CollectAsync_FailedStop_DoesNotDeleteSocketOrCount()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-orphan-collect-tests");
        try
        {
            var monitor = new TestAuxiliaryBackchannelMonitor();
            var stopper = new TestAppHostStopper { DefaultResult = false };

            var socketPath = CreateSocketFile(tempDir, "orphan.sock");
            monitor.AddConnection("orphan-hash", socketPath, CreateConnection(socketPath, appHostPid: 4242, orphaned: true));

            var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

            var collected = await collector.CollectAsync(CancellationToken.None);

            Assert.Equal(0, collected);
            // The process is not confirmed gone, so the socket must be left for a later cleanup pass.
            Assert.True(File.Exists(socketPath));
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CollectAsync_WhenStopThrows_SwallowsAndContinues()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-orphan-collect-tests");
        try
        {
            var monitor = new TestAuxiliaryBackchannelMonitor();

            var throwingSocket = CreateSocketFile(tempDir, "throwing.sock");
            var okSocket = CreateSocketFile(tempDir, "ok.sock");
            monitor.AddConnection("throwing-hash", throwingSocket, CreateConnection(throwingSocket, appHostPid: 1, orphaned: true));
            monitor.AddConnection("ok-hash", okSocket, CreateConnection(okSocket, appHostPid: 2, orphaned: true));

            var stopper = new TestAppHostStopper
            {
                ThrowSelector = info => info?.ProcessId == 1 ? new InvalidOperationException("boom") : null,
            };

            var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

            var collected = await collector.CollectAsync(CancellationToken.None);

            // Collection is best effort: one orphan throwing must not abort the rest.
            Assert.Equal(1, collected);
            Assert.True(File.Exists(throwingSocket));
            Assert.False(File.Exists(okSocket));
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CollectAsync_NoOrphans_ReturnsZeroWithoutStopping()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var stopper = new TestAppHostStopper();

        const string socketPath = "/tmp/aspire-orphan-tests-live.sock";
        monitor.AddConnection("live-hash", socketPath, CreateConnection(socketPath, appHostPid: 4343, orphaned: false));

        var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

        var collected = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(0, collected);
        Assert.Empty(stopper.StopRequests);
        Assert.Equal(1, monitor.ScanCallCount);
    }

    private static string CreateSocketFile(DirectoryInfo directory, string name)
    {
        var path = Path.Combine(directory.FullName, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(string socketPath, int appHostPid, bool orphaned)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            SocketPath = socketPath,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = appHostPid,
                CliProcessId = Environment.ProcessId,
                // Orphaned: a mismatched start time simulates a recycled CLI PID (the original launcher is gone).
                // Live: the current process's real start time matches, so the launching CLI still looks alive.
                CliStartedAt = orphaned
                    ? DateTimeOffset.FromUnixTimeSeconds(1)
                    : new DateTimeOffset(Process.GetCurrentProcess().StartTime),
            },
        };
    }
}
