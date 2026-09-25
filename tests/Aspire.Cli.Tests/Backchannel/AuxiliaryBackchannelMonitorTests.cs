// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Hosting.Backchannel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Backchannel;

public class AuxiliaryBackchannelMonitorTests
{
    [Fact]
    public void IsAppHostInScopeOfDirectory_WithSymlinkedPaths_IsInScope()
    {
        // The OS reports a process's current directory physically (for example macOS temp dirs under
        // /var -> /private/var), while a file-based AppHost reports its path unresolved. The in-scope check
        // must resolve symlinks on both operands or it treats an in-scope AppHost as out of scope, which made
        // CWD-based 'aspire describe' report "No running AppHost found". See https://github.com/microsoft/aspire/issues/17618.
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-symlink-");
        try
        {
            var realDirectory = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "real"));
            var symlinkDirectory = Path.Combine(tempRoot.FullName, "link");
            Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);

            // AppHost reported through the real directory, working directory reached through the symlink.
            var appHostPathViaReal = Path.Combine(realDirectory.FullName, "apphost.cs");
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(appHostPathViaReal, symlinkDirectory));

            // And the reverse: AppHost reached through the symlink, working directory the real path.
            var appHostPathViaSymlink = Path.Combine(symlinkDirectory, "apphost.cs");
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(appHostPathViaSymlink, realDirectory.FullName));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_AppHostOutsideWorkingDirectory_IsNotInScope()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-");
        try
        {
            var workingDirectory = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "wd")).FullName;
            var outsideAppHost = Path.Combine(tempRoot.FullName, "other", "apphost.cs");

            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(outsideAppHost, workingDirectory));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_NullOrEmptyAppHostPath_IsNotInScope()
    {
        Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(null, Path.GetTempPath()));
        Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(string.Empty, Path.GetTempPath()));
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_NestedLinkedWorktree_IsNotInScopeOfPrimary()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-worktree-");
        try
        {
            var primaryRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
            var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
            TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));

            var primaryAppHost = Path.Combine(primaryRoot, "AppHost.csproj");
            var nestedAppHost = Path.Combine(worktreeRoot, "AppHost.csproj");

            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(primaryAppHost, primaryRoot));
            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(nestedAppHost, primaryRoot));
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(nestedAppHost, worktreeRoot));
            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(primaryAppHost, worktreeRoot));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_Submodule_IsInScopeOfPrimary()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-submodule-");
        try
        {
            var primaryRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
            var submoduleRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, "extern", "dep")).FullName;
            TestGitWorktree.WriteGitDirFile(
                submoduleRoot,
                Path.Combine(primaryRoot, ".git", "modules", "dep"));

            var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(submoduleAppHost, primaryRoot));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_SubmoduleInsideLinkedWorktree_UsesEnclosingWorktree()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-linked-submodule-");
        try
        {
            var primaryRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
            var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
            var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));
            var submoduleRoot = Directory.CreateDirectory(Path.Combine(worktreeRoot, "extern", "dep")).FullName;
            TestGitWorktree.WriteGitDirFile(submoduleRoot, Path.Combine(adminDirectory, "modules", "dep"));
            var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");

            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(submoduleAppHost, worktreeRoot));
            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(submoduleAppHost, primaryRoot));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_UnreachableSocketWithLiveProcess_IsNotRetriedUntilBackoffExpires()
    {
        // A socket file whose PID is alive but that refuses connections is the PID-reuse shape:
        // the AppHost is gone, yet its PID was recycled by an unrelated process, so the monitor is
        // not allowed to delete the file (deleting a socket whose AppHost is actually alive makes
        // that AppHost undiscoverable for the rest of its lifetime). Before the backoff was added,
        // such a socket was pushed back onto the "new sockets" list on every scan, so every single
        // scan paid the full connect retry budget. MCP tools scan frequently, so that was seconds
        // of dead time per call, forever.
        var homeDirectory = CreateSocketSafeHomeDirectory();
        try
        {
            var socketPath = CreateLiveOwnerSocketPath(homeDirectory);

            // Bound but never listening, so every connect attempt is refused while the file stays on disk.
            using var unreachableSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unreachableSocket.Bind(new UnixDomainSocketEndPoint(socketPath));

            var logger = new CapturingLogger<AuxiliaryBackchannelMonitor>();
            var timeProvider = new FakeTimeProvider();
            using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
            using var monitor = new AuxiliaryBackchannelMonitor(logger, CreateExecutionContext(homeDirectory), timeProvider, profilingTelemetry);

            // The socket is discovered and the connect retry budget is burned down once.
            await PumpUntilCompletedAsync(monitor.ScanAsync(), timeProvider).DefaultTimeout();
            Assert.Equal(1, CountConnectAttempts(logger, socketPath));

            // The file must survive: its PID is alive, so the monitor cannot prove the socket is dead.
            Assert.True(File.Exists(socketPath));

            // The regression: a second scan inside the backoff window must not touch the socket at all.
            // If it did, this await would hang because the retry loop's delays run on the fake clock.
            await monitor.ScanAsync().DefaultTimeout();
            Assert.Equal(1, CountConnectAttempts(logger, socketPath));

            // Once the backoff expires the socket is reconsidered, so a genuinely restarted AppHost
            // reusing the same socket path is still picked up.
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            await PumpUntilCompletedAsync(monitor.ScanAsync(), timeProvider).DefaultTimeout();
            Assert.Equal(2, CountConnectAttempts(logger, socketPath));
        }
        finally
        {
            homeDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ConcurrentScansDoNotFanOutRetriesForTheSameSocket()
    {
        // Retry candidates are selected under _scanLock, but the connect attempts are awaited after the
        // lock is released and the backoff is only escalated once the retry budget is exhausted. That
        // leaves a window, as wide as the whole retry budget, in which another scan re-selects the same
        // socket and starts its own connect loop. MCP tools scan frequently enough to overlap, so a
        // single stale socket could still fan out concurrent retries and undo the backoff.
        var homeDirectory = CreateSocketSafeHomeDirectory();
        try
        {
            var socketPath = CreateLiveOwnerSocketPath(homeDirectory);

            using var unreachableSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unreachableSocket.Bind(new UnixDomainSocketEndPoint(socketPath));

            var logger = new CapturingLogger<AuxiliaryBackchannelMonitor>();
            var timeProvider = new FakeTimeProvider();
            using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
            using var monitor = new AuxiliaryBackchannelMonitor(logger, CreateExecutionContext(homeDirectory), timeProvider, profilingTelemetry);

            await PumpUntilCompletedAsync(monitor.ScanAsync(), timeProvider).DefaultTimeout();
            Assert.Equal(1, CountConnectAttempts(logger, socketPath));

            timeProvider.Advance(TimeSpan.FromMinutes(1));

            // The first scan claims the due retry and parks in its connect loop waiting on the fake clock.
            // Deliberately left unpumped so the claim is still in flight for the whole of the second scan.
            var claimingScan = monitor.ScanAsync();
            await WaitForConnectAttemptsAsync(logger, socketPath, expectedAttempts: 2).DefaultTimeout();

            // The overlapping scan must find nothing to do. Were it to re-select the claimed socket it
            // would start a second connect loop and this await would hang, because those retry delays
            // also run on the fake clock and nothing is advancing it.
            await monitor.ScanAsync().DefaultTimeout();
            Assert.Equal(2, CountConnectAttempts(logger, socketPath));

            await PumpUntilCompletedAsync(claimingScan, timeProvider).DefaultTimeout();
            Assert.Equal(2, CountConnectAttempts(logger, socketPath));
        }
        finally
        {
            homeDirectory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Waits until <paramref name="expectedAttempts"/> connect attempts have been made against
    /// <paramref name="socketPath"/>, failing with the captured log rather than hanging if they never arrive.
    /// </summary>
    private static async Task WaitForConnectAttemptsAsync(CapturingLogger<AuxiliaryBackchannelMonitor> logger, string socketPath, int expectedAttempts)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (CountConnectAttempts(logger, socketPath) < expectedAttempts)
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"Timed out waiting for {expectedAttempts} connect attempt(s) on '{socketPath}'. " +
                    $"Saw {CountConnectAttempts(logger, socketPath)}. Captured log:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, logger.Entries.Select(entry => $"  [{entry.Level}/{entry.EventId}] {entry.Message}")));
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static int CountConnectAttempts(CapturingLogger<AuxiliaryBackchannelMonitor> logger, string socketPath)
        => logger.Entries.Count(entry =>
            entry.EventId == AuxiliaryBackchannelMonitor.ConnectingToSocketEvent &&
            entry.Message.Contains(socketPath, StringComparison.Ordinal));

    /// <summary>
    /// Creates a stand-in home directory whose generated socket paths fit the platform's AF_UNIX byte limit.
    /// </summary>
    /// <remarks>
    /// The path the monitor ends up binding against is <c>{home}/.aspire/cli/bch/{19 chars}.{pid}</c>,
    /// which adds a fixed ~45 bytes on top of the home directory. macOS allows only 104 bytes for the
    /// whole path and its per-user temp root (<c>/var/folders/&lt;2&gt;/&lt;30&gt;/T</c>) already spends 48,
    /// so the prefix here is kept to a single character to stay inside the budget. Lengthening it puts
    /// the generated path over the limit on macOS and silently skips every test that calls this.
    /// </remarks>
    private static DirectoryInfo CreateSocketSafeHomeDirectory() => Directory.CreateTempSubdirectory("a");

    /// <summary>
    /// Builds a socket path under <paramref name="homeDirectory"/> whose embedded PID belongs to a
    /// process that is definitely alive, so the monitor is never permitted to delete the file.
    /// </summary>
    /// <remarks>
    /// The name is composed by hand rather than through <c>ComputeSocketPathFromAppHostId</c> because
    /// that helper throws on an over-long path, and whether the path fits is exactly what the skip below
    /// needs to decide. Callers bind a real AF_UNIX socket here: connecting to a regular file fails with
    /// ENOTSOCK rather than ECONNREFUSED and would take a different code path entirely.
    /// </remarks>
    private static string CreateLiveOwnerSocketPath(DirectoryInfo homeDirectory)
    {
        var backchannelsDirectory = BackchannelConstants.GetBackchannelsDirectory(homeDirectory.FullName);
        Directory.CreateDirectory(backchannelsDirectory);

        var appHostId = BackchannelConstants.ComputeAppHostId(Path.Combine(homeDirectory.FullName, "MyApp.AppHost.csproj"));
        var socketPath = Path.Combine(backchannelsDirectory, $"{appHostId}a1b2C3d4.{Environment.ProcessId}");
        Assert.SkipWhen(
            BackchannelConstants.GetSocketPathByteCountIncludingNull(socketPath) > BackchannelConstants.GetMaxSocketPathBytesIncludingNull(),
            $"The temp directory is too long to host an AF_UNIX socket on this platform: '{socketPath}'.");

        return socketPath;
    }

    /// <summary>
    /// Drives <paramref name="task"/> to completion while advancing <paramref name="timeProvider"/>,
    /// which the monitor's connect retry loop uses for both its elapsed-time budget and its delays.
    /// </summary>
    private static async Task PumpUntilCompletedAsync(Task task, FakeTimeProvider timeProvider)
    {
        while (!task.IsCompleted)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            // Yield on the real clock so the retry loop can observe the advance and register its next delay.
            await Task.Delay(1).ConfigureAwait(false);
        }

        await task.ConfigureAwait(false);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo homeDirectory)
        => new(
            workingDirectory: homeDirectory,
            hivesDirectory: homeDirectory,
            cacheDirectory: homeDirectory,
            sdksDirectory: homeDirectory,
            logsDirectory: homeDirectory,
            logFilePath: Path.Combine(homeDirectory.FullName, "test.log"),
            identityChannel: "local",
            homeDirectory: homeDirectory);
}
