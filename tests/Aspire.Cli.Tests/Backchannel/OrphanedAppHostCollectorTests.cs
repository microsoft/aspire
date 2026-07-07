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
    public void IsOrphaned_StableStartTimeMatchesLiveCli_ReturnsFalse()
    {
        // Current AppHosts report CliStableStartedAt (the launcher CLI's /proc-based identity). The
        // current process stands in for a launching CLI that is still alive, so its stable start time
        // matches exactly and the AppHost is not orphaned.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = Environment.ProcessId,
                CliStableStartedAt = GetCurrentProcessStableStartTime(),
            },
        };

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_StableStartTimeMismatchOnLiveCli_ReturnsTrue()
    {
        // The PID is alive but its stable /proc start time does not match — a recycled PID. The stable
        // identity is immune to clock drift, so this mismatch is trustworthy evidence the original
        // launcher is gone and the AppHost is orphaned.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = Environment.ProcessId,
                CliStableStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1),
            },
        };

        Assert.True(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_StablePreferredOverMismatchedLegacy_ReturnsFalse()
    {
        // Both identities are present. The stable value matches the live CLI while the legacy value does
        // not (as can happen after a clock step). The collector must trust the drift-immune stable value
        // and leave the live AppHost alone.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = Environment.ProcessId,
                CliStableStartedAt = GetCurrentProcessStableStartTime(),
                CliStartedAt = DateTimeOffset.FromUnixTimeSeconds(1),
            },
        };

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_LegacyLiveCliProcess_ReturnsFalse()
    {
        // Older AppHost: only the legacy CliStartedAt is reported. The current process stands in for a
        // launching CLI that is still alive, so the AppHost is not orphaned.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = Environment.ProcessId,
                CliStartedAt = GetCurrentProcessRuntimeStartTime(),
            },
        };

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_LegacyMismatchedStartTimeOnLiveCli_ReturnsFalse()
    {
        // Older AppHost: only the drift-prone legacy CliStartedAt is available and it does NOT match the
        // live CLI PID (as happens after a wall-clock step, not just PID reuse). Collecting is
        // destructive, so an ambiguous legacy mismatch on a still-live PID must NOT tear down the app.
        // The collector only acts on the legacy path when the CLI PID is entirely gone.
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

        Assert.False(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_DeadCliProcess_ReturnsTrue()
    {
        // A dead launcher: start the shared bounded, self-terminating helper (so an aborted test host
        // can't leak it), capture its start time, then kill it. The collector must then see the
        // recorded PID + start time as no-longer-running and report the AppHost orphaned.
        using var cliProcess = TestProcesses.StartLongRunning();

        var cliStartedAt = GetProcessStartTime(cliProcess);
        cliProcess.Kill(entireProcessTree: true);
        cliProcess.WaitForExit();

        // The launching CLI PID is entirely gone, so the AppHost is orphaned regardless of which
        // identity domain was reported.
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/tmp/AppHost.csproj",
                ProcessId = 4242,
                CliProcessId = cliProcess.Id,
                CliStableStartedAt = cliStartedAt,
            },
        };

        Assert.True(OrphanedAppHostCollector.IsOrphaned(connection));
    }

    [Fact]
    public void IsOrphaned_LegacyDeadCliProcess_ReturnsTrue()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t localhost") { CreateNoWindow = true }
            : new ProcessStartInfo("tail", "-f /dev/null") { CreateNoWindow = true };
        using var cliProcess = Process.Start(psi);
        Assert.NotNull(cliProcess);

        var cliStartedAt = GetProcessStartTime(cliProcess);
        cliProcess.Kill(entireProcessTree: true);
        cliProcess.WaitForExit();

        // Older AppHost reporting only the legacy identity: the PID is gone, so it is orphaned.
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

    [Fact]
    public async Task CollectAsync_WhenScanThrows_ReturnsZero()
    {
        // Collection is best effort: a non-cancellation scan failure must not surface to the caller
        // (e.g. `aspire ps`), even when an orphan is present that would otherwise have been collected.
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            ScanAsyncCallback = _ => throw new InvalidOperationException("scan boom")
        };
        var stopper = new TestAppHostStopper();

        const string socketPath = "/tmp/aspire-orphan-tests-scan-throw.sock";
        monitor.AddConnection("orphan-hash", socketPath, CreateConnection(socketPath, appHostPid: 4242, orphaned: true));

        var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

        var collected = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(0, collected);
        Assert.Empty(stopper.StopRequests);
    }

    [Fact]
    public async Task CollectAsync_WhenScanCancelled_Throws()
    {
        // Cancellation is the one failure that must NOT be swallowed, so callers can abort promptly.
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            ScanAsyncCallback = _ => throw new OperationCanceledException()
        };
        var stopper = new TestAppHostStopper();

        var collector = new OrphanedAppHostCollector(monitor, stopper, NullLogger<OrphanedAppHostCollector>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => collector.CollectAsync(CancellationToken.None));
        Assert.Empty(stopper.StopRequests);
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
                // Drive the decision through the stable /proc identity the collector prefers.
                // Orphaned: a mismatched stable start time is trustworthy evidence of a recycled CLI PID.
                // Live: the current process's real stable start time matches, so the launching CLI is alive.
                CliStableStartedAt = orphaned
                    ? DateTimeOffset.FromUnixTimeMilliseconds(1)
                    : GetCurrentProcessStableStartTime(),
            },
        };
    }

    private static DateTimeOffset GetCurrentProcessRuntimeStartTime()
        => DateTimeOffset.FromUnixTimeSeconds(ProcessStartTimeHelper.GetCurrentProcessRuntimeStartTimeUnixSeconds());

    private static DateTimeOffset GetCurrentProcessStableStartTime()
        => DateTimeOffset.FromUnixTimeMilliseconds(ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds());

    private static DateTimeOffset GetProcessStartTime(Process process)
    {
        var startTime = ProcessStartTimeHelper.TryGetProcessStartTime(process.Id);
        Assert.NotNull(startTime);
        return startTime.Value;
    }
}
