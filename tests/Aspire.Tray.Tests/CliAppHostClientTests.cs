// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Tray.Tests.Helpers;
using Microsoft.Extensions.Time.Testing;
using static Aspire.Tray.Tests.CliProtocolTests;

namespace Aspire.Tray.Tests;

public class CliAppHostClientTests
{
    public static bool SupportsShell => !OperatingSystem.IsWindows();

    [Fact]
    public void WatchOptsIntoTheVersionedContractWithoutAShell()
    {
        var executable = Path.GetFullPath("cli path/aspire");
        var startInfo = CliAppHostClient.CreateWatchStartInfo(executable);
        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(["ps", "--follow", "--format", "json", "--output", "snapshot", "--non-interactive", "--nologo"],
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), startInfo.WorkingDirectory);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task StartUsesTheConfiguredCliWithoutCreatingADiscoveryStream()
    {
        using var directory = new TestTrayStateDirectory();
        var appHost = directory.CreateAppHost("apphost.cs");
        using var cli = new FixtureCli("exit 0");
        IAppHostClient client = new CliAppHostClient(cli.Path);

        Assert.Equal(new StartResult(StartOutcome.Started, 0),
            await client.StartAsync(appHost, TestContext.Current.CancellationToken));
        Assert.Equal(["start", "--apphost", appHost, "--non-interactive", "--nologo"], cli.ReadArguments());
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task EmptySnapshotIsLiveAndDisposingTheReaderCleansUpItsChild()
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"type":"snapshot","appHosts":[]}\n'
            exec /bin/sleep 60
            """);
        var client = new CliAppHostClient(cli.Path);
        var stream = client.WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Connecting, stream.Current.Discovery);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);
        Assert.Empty(stream.Current.AppHosts);
        using var process = Process.GetProcessById(await cli.WaitForPidAsync(TestContext.Current.CancellationToken));
        await stream.DisposeAsync();
        Assert.True(process.HasExited);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task EofKeepsTheLastSnapshotButMarksItDisconnected()
    {
        using var cli = new FixtureCli($"printf '%s\\n' '{Snapshot(Host(42))}'");
        var stream = new CliAppHostClient(cli.Path)
            .WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);
        Assert.Equal(42, Assert.Single(stream.Current.AppHosts).AppHostPid);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
        Assert.Equal(42, Assert.Single(stream.Current.AppHosts).AppHostPid);
    }

    [Theory(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    [InlineData("exit 0")]
    [InlineData("printf 'unsupported option\\n' >&2; exit 1")]
    [InlineData("printf '{\"version\":2,\"type\":\"snapshot\",\"appHosts\":[]}\\n'")]
    [InlineData("printf '{\"version\":1,\"type\":\"heartbeat\"}\\n'")]
    [InlineData("printf 'Unexpected diagnostic on stdout\\n'")]
    public async Task IncompatibleCliFailsClosedWithoutRetrying(string script)
    {
        using var cli = new FixtureCli(script);
        var stream = new CliAppHostClient(cli.Path)
            .WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Incompatible, stream.Current.Discovery);
        Assert.Empty(stream.Current.AppHosts);
        Assert.False(await stream.MoveNextAsync());
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task HeartbeatsDoNotCreatePresentationUpdates()
    {
        using var cli = new FixtureCli($$"""
            printf '{"version":1,"type":"snapshot","appHosts":[]}\n'
            printf '{"version":1,"type":"heartbeat"}\n'
            printf '%s\n' '{{Snapshot(Host(42))}}'
            """);
        var stream = new CliAppHostClient(cli.Path)
            .WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Empty(stream.Current.AppHosts);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);
        Assert.Equal(42, Assert.Single(stream.Current.AppHosts).AppHostPid);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task DiscoveryFailureNeverBecomesAnEmptyLiveList()
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"type":"error","errorCode":"discovery_failed"}\n'
            exit 1
            """);
        var stream = new CliAppHostClient(cli.Path)
            .WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task LimitFailureIsExplicitAndDoesNotRestartTheSameOversizedQuery()
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"type":"error","errorCode":"limit_exceeded"}\n'
            exit 1
            """);
        var stream = new CliAppHostClient(cli.Path)
            .WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.LimitExceeded, stream.Current.Discovery);
        Assert.False(await stream.MoveNextAsync());
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task ReconnectReplacesThePreviousListInOneSnapshot()
    {
        using var cli = new FixtureCli($$"""
            if [ -e "$0.attempt" ]; then
                printf '%s\n' '{{Snapshot(Host(43))}}'
                exec /bin/sleep 60
            fi
            printf 'first\n' > "$0.attempt"
            printf '%s\n' '{{Snapshot(Host(42))}}'
            """);
        var client = new CliAppHostClient(cli.Path, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10), TimeProvider.System);
        var stream = client.WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(42, Assert.Single(stream.Current.AppHosts).AppHostPid);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Connecting, stream.Current.Discovery);
        Assert.Equal(42, Assert.Single(stream.Current.AppHosts).AppHostPid);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);
        Assert.Equal(43, Assert.Single(stream.Current.AppHosts).AppHostPid);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task QuietHungWatcherTimesOutAndItsProcessIsCleanedUp()
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"type":"snapshot","appHosts":[]}\n'
            exec /bin/sleep 60
            """);
        var client = new CliAppHostClient(cli.Path, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeProvider.System);
        var stream = client.WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        using var process = Process.GetProcessById(await cli.WaitForPidAsync(TestContext.Current.CancellationToken));
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
        Assert.True(process.HasExited);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task CancellationWhileConnectingJoinsTheWatcherChild()
    {
        using var cli = new FixtureCli("exec /bin/sleep 60");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new CliAppHostClient(cli.Path).WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        await using var lifetime = stream.ConfigureAwait(true);
        Assert.True(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        using var process = Process.GetProcessById(await cli.WaitForPidAsync(TestContext.Current.CancellationToken));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.True(process.HasExited);
    }

    [Theory(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortLivedSessionsRetainExponentialBackoff(bool emitHeartbeat)
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"type":"snapshot","appHosts":[]}\n'
            """ + (emitHeartbeat ? """

            printf '{"version":1,"type":"heartbeat"}\n'
            """ : ""));
        var time = new FakeTimeProvider();
        var client = new CliAppHostClient(cli.Path, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), time);
        await using var stream = client.WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Connecting, stream.Current.Discovery);

        foreach (var seconds in new[] { 1, 2, 4, 8, 10, 10 })
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
            var reconnect = stream.MoveNextAsync().AsTask();
            time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            Assert.False(reconnect.IsCompleted);
            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.True(await reconnect.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(DiscoveryState.Connecting, stream.Current.Discovery);
        }
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task SustainedHeartbeatResetsRetryBackoff()
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"type":"snapshot","appHosts":[]}\n'
            if [ -e "$0.attempt" ]; then
                while [ ! -e "$0.heartbeat" ]; do /bin/sleep 0.01; done
                printf '{"version":1,"type":"heartbeat"}\n'
                printf '{"version":1,"type":"snapshot","appHosts":[]}\n'
            fi
            printf 'first\n' > "$0.attempt"
            """);
        var time = new FakeTimeProvider();
        var client = new CliAppHostClient(cli.Path, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), time);
        await using var stream = client.WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
        var reconnect = stream.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await reconnect.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);

        time.Advance(TimeSpan.FromSeconds(30));
        File.WriteAllText(cli.Path + ".heartbeat", "ready");
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Live, stream.Current.Discovery);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(DiscoveryState.Disconnected, stream.Current.Discovery);
        reconnect = stream.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(reconnect.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(await reconnect.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DiscoveryState.Connecting, stream.Current.Discovery);
    }
}
