// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Backchannel;

public class TrayWatchStreamTests
{
    [Fact]
    public async Task EmptyInitialSnapshotPrecedesHeartbeatAndQuietBrokenPipeStopsWatcher()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var time = new FakeTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.WatchConnectionsHandler = Watch;
        var messages = new List<TrayWatchMessage>();
        var stream = CreateStream(monitor, time);
        var run = stream.RunAsync((json, _) =>
        {
            var message = Deserialize(json);
            messages.Add(message);
            if (message.Type == "heartbeat")
            {
                throw new IOException("Broken stdout.");
            }
            return Task.CompletedTask;
        }, CancellationToken.None);

        await waiting.Task.DefaultTimeout();
        Assert.Collection(messages, message =>
        {
            Assert.Equal("snapshot", message.Type);
            Assert.Empty(message.AppHosts!);
        });
        time.Advance(TrayCliProtocol.HeartbeatInterval);
        Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        await disposed.Task.DefaultTimeout();
        Assert.Equal(["snapshot", "heartbeat"], messages.Select(message => message.Type));
        Assert.True(monitor.LastWatchReadOnly);

        async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> Watch([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return [];
                waiting.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                disposed.SetResult();
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClosedSnapshotOutputSucceedsAndObservesWatcher(bool afterInitial, bool disposedOutput)
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = Watch };
        var messages = new List<TrayWatchMessage>();

        var result = await CreateStream(monitor, new FakeTimeProvider()).RunAsync((json, _) =>
        {
            messages.Add(Deserialize(json));
            if (!afterInitial || messages.Count == 2)
            {
                throw disposedOutput ? new ObjectDisposedException("stdout") : new IOException("Broken stdout.");
            }
            return Task.CompletedTask;
        }, CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        await disposed.Task.DefaultTimeout();
        Assert.Equal(afterInitial ? ["snapshot", "snapshot"] : ["snapshot"], messages.Select(message => message.Type));

        async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> Watch([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return [];
                yield return [Connection("/project/a.cs", 10)];
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                disposed.SetResult();
            }
        }
    }

    [Fact]
    public async Task SnapshotsReplaceStateIncludingRemovalsAndAreDeterministicallyOrdered()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshots = Channel.CreateUnbounded<IReadOnlyList<IAppHostAuxiliaryBackchannel>>();
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = token => snapshots.Reader.ReadAllAsync(token) };
        var first = Connection("/project/a.cs", 10);
        var second = Connection("/project/b.cs", 20);
        snapshots.Writer.TryWrite([second, first]);
        var messages = new List<TrayWatchMessage>();
        var stream = CreateStream(monitor, new FakeTimeProvider(), new TestProcessIdentityProvider { GetStartTime = pid => pid == 10 ? 1000 : null });

        var result = await stream.RunAsync((json, _) =>
        {
            var message = Deserialize(json);
            messages.Add(message);
            if (messages.Count == 1)
            {
                snapshots.Writer.TryWrite([second]);
            }
            else if (messages.Count == 2)
            {
                snapshots.Writer.TryWrite([]);
            }
            else
            {
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        }, cancellation.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Collection(messages,
            message =>
            {
                Assert.Equal("snapshot", message.Type);
                Assert.Collection(message.AppHosts!,
                    host => { Assert.Equal(10, host.AppHostPid); Assert.Equal(1000, host.ProcessStartTimeUnixMilliseconds); },
                    host => { Assert.Equal(20, host.AppHostPid); Assert.Null(host.ProcessStartTimeUnixMilliseconds); });
            },
            message => Assert.Equal(20, Assert.Single(message.AppHosts!).AppHostPid),
            message => Assert.Empty(message.AppHosts!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DiscoveryFailureIsTerminalAndNeverAnEmptySnapshot(bool afterInitial, bool outputClosed)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = Watch };
        var messages = new List<TrayWatchMessage>();
        var result = await CreateStream(monitor, new FakeTimeProvider()).RunAsync((json, _) =>
        {
            var message = Deserialize(json);
            messages.Add(message);
            if (outputClosed && message.Type == "error")
            {
                throw new IOException("Broken stdout.");
            }
            return Task.CompletedTask;
        }, CancellationToken.None).DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, result);
        Assert.Equal(afterInitial ? 2 : 1, messages.Count);
        var error = messages[^1];
        Assert.Equal("error", error.Type);
        Assert.Equal("discovery_failed", error.ErrorCode);
        Assert.Null(error.AppHosts);
        if (afterInitial)
        {
            Assert.Single(messages[0].AppHosts!);
        }

        async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> Watch([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            token.ThrowIfCancellationRequested();
            if (afterInitial)
            {
                yield return [Connection("/project/a.cs", 10)];
            }
            throw new IOException("Discovery is unavailable.");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LimitsProduceTerminalErrorWithoutTruncation(bool tooManyHosts)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        if (tooManyHosts)
        {
            for (var i = 0; i <= TrayCliProtocol.MaximumAppHosts; i++)
            {
                var connection = Connection("/project/a.cs", i + 1);
                monitor.AddConnection(connection.SocketPath, connection);
            }
        }
        else
        {
            var connection = Connection("/" + new string('x', TrayCliProtocol.MaximumMessageLength), 1);
            monitor.AddConnection(connection.SocketPath, connection);
        }
        var messages = new List<TrayWatchMessage>();

        var result = await CreateStream(monitor, new FakeTimeProvider()).RunAsync((json, _) =>
        {
            Assert.True(json.Length <= TrayCliProtocol.MaximumMessageLength);
            messages.Add(Deserialize(json));
            return Task.CompletedTask;
        }, CancellationToken.None).DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, result);
        var error = Assert.Single(messages);
        Assert.Equal("error", error.Type);
        Assert.Equal("limit_exceeded", error.ErrorCode);
        Assert.Null(error.AppHosts);
    }

    [Fact]
    public async Task SlowConsumerCoalescesToLatestSnapshotAndCancellationJoinsProducer()
    {
        using var cancellation = new CancellationTokenSource();
        var slowWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerCaughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = Watch };
        var received = new List<int>();
        var run = CreateStream(monitor, new FakeTimeProvider()).RunAsync(async (json, token) =>
        {
            received.Add(Assert.Single(Deserialize(json).AppHosts!).AppHostPid);
            if (received.Count == 2)
            {
                slowWrite.SetResult();
                await producerCaughtUp.Task.WaitAsync(token);
            }
            if (received.Count == 3)
            {
                cancellation.Cancel();
            }
        }, cancellation.Token);

        Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        await disposed.Task.DefaultTimeout();
        Assert.Equal([1, 2, 100], received);

        async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> Watch([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return [Connection("/project/a.cs", 1)];
                yield return [Connection("/project/a.cs", 2)];
                await slowWrite.Task.WaitAsync(token);
                for (var i = 3; i <= 100; i++)
                {
                    yield return [Connection("/project/a.cs", i)];
                }
                producerCaughtUp.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                disposed.SetResult();
            }
        }
    }

    [Fact]
    public async Task CancellationDuringBackpressuredWriteObservesWatcher()
    {
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = Watch };
        var writes = 0;
        var run = CreateStream(monitor, new FakeTimeProvider()).RunAsync(async (_, token) =>
        {
            if (++writes == 2)
            {
                blocked.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }, cancellation.Token);

        await blocked.Task.DefaultTimeout();
        cancellation.Cancel();
        Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        await disposed.Task.DefaultTimeout();
        Assert.Equal(2, writes);

        async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> Watch([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return [];
                yield return [Connection("/project/a.cs", 1)];
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                disposed.SetResult();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DashboardTimeoutDoesNotHideHealthyPeersInInitialSnapshot(bool ignoresCancellation)
    {
        var time = new FakeTimeProvider();
        var logger = new FakeLogger();
        var hungStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<DashboardUrlsState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lookupToken = default;
        var hung = Connection("/project/a.cs", 10);
        hung.GetDashboardUrlsHandler = token =>
        {
            lookupToken = token;
            hungStarted.SetResult();
            return ignoresCancellation ? pending.Task : pending.Task.WaitAsync(token);
        };
        var healthy = Connection("/project/b.cs", 20);
        healthy.GetDashboardUrlsHandler = _ =>
        {
            healthyStarted.SetResult();
            return Task.FromResult<DashboardUrlsState?>(new() { BaseUrlWithLoginToken = "http://localhost:1234" });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(hung.SocketPath, hung);
        monitor.AddConnection(healthy.SocketPath, healthy);
        TrayWatchMessage? snapshot = null;
        var run = new TrayWatchStream(monitor, new TestProcessIdentityProvider(), time, logger).RunAsync((json, _) =>
        {
            snapshot = Deserialize(json);
            throw new IOException("Closed after initial snapshot.");
        }, CancellationToken.None);

        try
        {
            await Task.WhenAll(hungStarted.Task, healthyStarted.Task).DefaultTimeout();
            Assert.False(run.IsCompleted);
            time.Advance(TrayWatchStream.DashboardLookupTimeout);

            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
            Assert.True(lookupToken.IsCancellationRequested);
            Assert.Equal("snapshot", snapshot!.Type);
            Assert.Collection(snapshot.AppHosts!,
                host => { Assert.Equal(10, host.AppHostPid); Assert.Null(host.DashboardUrl); },
                host => { Assert.Equal(20, host.AppHostPid); Assert.Equal("http://localhost:1234", host.DashboardUrl); });
            var timeoutLog = Assert.Single(logger.Collector.GetSnapshot(), record => record.Message.StartsWith("Dashboard URL lookup", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Debug, timeoutLog.Level);
            Assert.Equal("Dashboard URL lookup timed out or was canceled for AppHost PID 10.", timeoutLog.Message);
            Assert.True(monitor.LastWatchReadOnly);
            Assert.Equal(0, hung.StopAppHostCallCount);
            Assert.Equal(0, healthy.StopAppHostCallCount);
        }
        finally
        {
            pending.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParentCancellationDuringDashboardLookupObservesWatcher(bool afterInitial, bool ignoresCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new FakeLogger();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<DashboardUrlsState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lookupToken = default;
        var hung = Connection("/project/a.cs", 10);
        hung.GetDashboardUrlsHandler = async token =>
        {
            try
            {
                lookupToken = token;
                started.SetResult();
                if (ignoresCancellation)
                {
                    return await pending.Task;
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
            finally
            {
                lookupStopped.SetResult();
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = Watch };
        var messages = new List<TrayWatchMessage>();
        var run = new TrayWatchStream(monitor, new TestProcessIdentityProvider(), new FakeTimeProvider(), logger).RunAsync((json, _) =>
        {
            messages.Add(Deserialize(json));
            return Task.CompletedTask;
        }, cancellation.Token);

        try
        {
            await started.Task.DefaultTimeout();
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
            Assert.True(lookupToken.IsCancellationRequested);
            Assert.Equal(afterInitial ? ["snapshot"] : [], messages.Select(message => message.Type));
            Assert.Empty(logger.Collector.GetSnapshot());
        }
        finally
        {
            cancellation.Cancel();
            pending.TrySetResult(null);
        }
        await Task.WhenAll(lookupStopped.Task, disposed.Task).DefaultTimeout();

        async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> Watch([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                if (afterInitial)
                {
                    yield return [];
                }
                yield return [hung];
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                disposed.SetResult();
            }
        }
    }

    [Fact]
    public async Task DashboardSnapshotBudgetBoundsQueuedLookupsWithoutDroppingHosts()
    {
        var time = new FakeTimeProvider();
        var logger = new FakeLogger();
        var batches = Channel.CreateUnbounded<int>();
        var calls = 0;
        var monitor = new TestAuxiliaryBackchannelMonitor();
        for (var i = 0; i < TrayCliProtocol.MaximumAppHosts; i++)
        {
            var connection = Connection("/project/a.cs", i + 1);
            connection.GetDashboardUrlsHandler = async token =>
            {
                // Each call completes before its own deadline, but 1,000 calls still require
                // a whole-snapshot budget. Register the delay before releasing the test clock.
                var delay = Task.Delay(TimeSpan.FromSeconds(1), time, token);
                var count = Interlocked.Increment(ref calls);
                if (count % TrayWatchStream.MaximumConcurrentDashboardLookups == 0)
                {
                    batches.Writer.TryWrite(count);
                }
                await delay;
                return null;
            };
            monitor.AddConnection(connection.SocketPath, connection);
        }
        TrayWatchMessage? snapshot = null;
        var run = new TrayWatchStream(monitor, new TestProcessIdentityProvider(), time, logger).RunAsync((json, _) =>
        {
            snapshot = Deserialize(json);
            throw new IOException("Closed after initial snapshot.");
        }, CancellationToken.None);

        for (var second = 0; second < TrayWatchStream.DashboardSnapshotTimeout.TotalSeconds; second++)
        {
            Assert.Equal((second + 1) * TrayWatchStream.MaximumConcurrentDashboardLookups, await batches.Reader.ReadAsync().AsTask().DefaultTimeout());
            Assert.False(run.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        Assert.Equal("snapshot", snapshot!.Type);
        Assert.Equal(Enumerable.Range(1, TrayCliProtocol.MaximumAppHosts), snapshot.AppHosts!.Select(host => host.AppHostPid));
        Assert.All(snapshot.AppHosts!, host => Assert.Null(host.DashboardUrl));
        Assert.InRange(calls, TrayWatchStream.MaximumConcurrentDashboardLookups, TrayCliProtocol.MaximumAppHosts - 1);
        var budgetLog = Assert.Single(logger.Collector.GetSnapshot(), record => record.Message.StartsWith("Dashboard URL snapshot", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, budgetLog.Level);
        Assert.True(TrayWatchStream.DashboardSnapshotTimeout < TrayCliProtocol.LivenessTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnacknowledgedDashboardCancellationCannotAccumulateRequestsAcrossSnapshots(bool saturateSlots)
    {
        using var cancellation = new CancellationTokenSource();
        var time = new FakeTimeProvider();
        var pending = new TaskCompletionSource<DashboardUrlsState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = Channel.CreateUnbounded<IReadOnlyList<IAppHostAuxiliaryBackchannel>>();
        var messages = Channel.CreateUnbounded<TrayWatchMessage>();
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = token => snapshots.Reader.ReadAllAsync(token) };
        var blockedCount = saturateSlots ? TrayWatchStream.MaximumConcurrentDashboardLookups : 1;
        var calls = 0;
        var hung = Enumerable.Range(1, blockedCount).Select(pid =>
        {
            var connection = Connection("/project/a.cs", pid);
            connection.GetDashboardUrlsHandler = _ =>
            {
                if (Interlocked.Increment(ref calls) == blockedCount)
                {
                    started.SetResult();
                }
                return pending.Task;
            };
            return connection;
        }).ToArray();
        var healthy = Connection("/project/b.cs", 20);
        healthy.DashboardUrlsState = new() { BaseUrlWithLoginToken = "http://localhost:1234" };
        snapshots.Writer.TryWrite(hung);
        var run = CreateStream(monitor, time).RunAsync((json, _) =>
        {
            messages.Writer.TryWrite(Deserialize(json));
            return Task.CompletedTask;
        }, cancellation.Token);

        try
        {
            await started.Task.DefaultTimeout();
            time.Advance(TrayWatchStream.DashboardLookupTimeout);
            var initial = await messages.Reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal(blockedCount, initial.AppHosts!.Count);
            Assert.All(initial.AppHosts, host => Assert.Null(host.DashboardUrl));

            for (var i = 0; i < 3; i++)
            {
                var newcomer = Connection("/project/c.cs", 100 + i);
                if (saturateSlots)
                {
                    newcomer.GetDashboardUrlsHandler = _ =>
                    {
                        Interlocked.Increment(ref calls);
                        return Task.FromResult<DashboardUrlsState?>(null);
                    };
                }
                // Retrying the same live connection must not overlap its pending RPC. With
                // all slots occupied, even new connections must leave enrichment unavailable.
                snapshots.Writer.TryWrite(saturateSlots ? [newcomer] : [hung[0], healthy, newcomer]);
                var snapshot = await messages.Reader.ReadAsync().AsTask().DefaultTimeout();

                Assert.Equal("snapshot", snapshot.Type);
                if (saturateSlots)
                {
                    var host = Assert.Single(snapshot.AppHosts!);
                    Assert.Equal(100 + i, host.AppHostPid);
                    Assert.Null(host.DashboardUrl);
                }
                else
                {
                    Assert.Collection(snapshot.AppHosts!,
                        host => { Assert.Equal(1, host.AppHostPid); Assert.Null(host.DashboardUrl); },
                        host => { Assert.Equal(20, host.AppHostPid); Assert.Equal("http://localhost:1234", host.DashboardUrl); },
                        host => Assert.Equal(100 + i, host.AppHostPid));
                }
                Assert.Equal(blockedCount, Volatile.Read(ref calls));
            }
        }
        finally
        {
            cancellation.Cancel();
            pending.TrySetResult(null);
        }
        Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
    }

    [Fact]
    public async Task DashboardRpcFailureIsLoggedWithoutFailingDiscovery()
    {
        var logger = new FakeLogger();
        var failure = new IOException("Dashboard not available.");
        var connection = Connection("/project/a.cs", 10);
        connection.GetDashboardUrlsHandler = _ => Task.FromException<DashboardUrlsState?>(failure);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.SocketPath, connection);
        TrayWatchMessage? snapshot = null;

        var result = await new TrayWatchStream(monitor, new TestProcessIdentityProvider(), new FakeTimeProvider(), logger).RunAsync((json, _) =>
        {
            snapshot = Deserialize(json);
            throw new IOException("Closed after initial snapshot.");
        }, CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal("snapshot", snapshot!.Type);
        Assert.Null(Assert.Single(snapshot.AppHosts!).DashboardUrl);
        var failureLog = Assert.Single(logger.Collector.GetSnapshot(), record => record.Message.StartsWith("Dashboard URL unavailable", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, failureLog.Level);
        Assert.Same(failure, failureLog.Exception);
    }

    [Fact]
    public async Task ResourceHealthTransitionsPublishWithoutDiscoveryChangesOrPolling()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshots = Channel.CreateUnbounded<IReadOnlyList<IAppHostAuxiliaryBackchannel>>();
        var resourceUpdates = Channel.CreateUnbounded<ResourceSnapshot>();
        var messages = Channel.CreateUnbounded<TrayWatchMessage>();
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = Connection("/project/a.cs", 10);
        connection.SupportsResourceSnapshotVersionsV1 = true;
        connection.ResourceSnapshots =
        [
            new() { Name = "api", State = "Starting", Version = 1 },
            new() { Name = "cache", State = "Running", HealthStatus = "Healthy", Version = 1 }
        ];
        var watchCalls = 0;
        var dashboardCalls = 0;
        connection.WatchResourceSnapshotsHandler = Watch;
        connection.GetDashboardUrlsHandler = _ =>
        {
            Interlocked.Increment(ref dashboardCalls);
            return Task.FromResult<DashboardUrlsState?>(null);
        };
        snapshots.Writer.TryWrite([connection]);
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = token => snapshots.Reader.ReadAllAsync(token) };
        var run = CreateStream(monitor, new FakeTimeProvider(), new TestProcessIdentityProvider { GetStartTime = _ => 1000 })
            .RunAsync((json, _) =>
            {
                messages.Writer.TryWrite(Deserialize(json));
                return Task.CompletedTask;
            }, cancellation.Token);

        try
        {
            var initial = await ReadHostAsync();
            Assert.Equal("warning", initial.Health);
            Assert.Equal(1000, initial.ProcessStartTimeUnixMilliseconds);

            resourceUpdates.Writer.TryWrite(new() { Name = "api", State = "Running", HealthStatus = "Healthy", Version = 2 });
            Assert.Equal(initial with { Health = "healthy" }, await ReadHostAsync());

            resourceUpdates.Writer.TryWrite(new() { Name = "cache", State = "Running", HealthStatus = "Degraded", Version = 2 });
            Assert.Equal(initial with { Health = "warning" }, await ReadHostAsync());

            resourceUpdates.Writer.TryWrite(new() { Name = "cache", State = "Running", HealthStatus = "Unhealthy", Version = 3 });
            Assert.Equal(initial with { Health = "unhealthy" }, await ReadHostAsync());

            resourceUpdates.Writer.TryWrite(new() { Name = "cache", State = "Running", HealthStatus = "Healthy", Version = 4 });
            Assert.Equal(initial with { Health = "healthy" }, await ReadHostAsync());

            resourceUpdates.Writer.TryWrite(new() { Name = "api", State = "Running", HealthStatus = "Unhealthy", Version = 1 });
            resourceUpdates.Writer.TryWrite(new() { Name = "cache", State = "FailedToStart", Version = 5 });
            Assert.Equal(initial with { Health = "unhealthy" }, await ReadHostAsync());

            resourceUpdates.Writer.TryWrite(new() { Name = "cache", State = "FailedToStart", IsHidden = true, Version = 6 });
            Assert.Equal(initial with { Health = "healthy" }, await ReadHostAsync());

            resourceUpdates.Writer.TryComplete();
            Assert.Equal(initial with { Health = null }, await ReadHostAsync());
            Assert.Equal(1, connection.GetResourceSnapshotsCallCount);
            Assert.Equal(1, Volatile.Read(ref watchCalls));
            Assert.Equal(1, Volatile.Read(ref dashboardCalls));
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        }
        await watchStopped.Task.DefaultTimeout();

        async Task<TrayAppHost> ReadHostAsync()
        {
            var message = await messages.Reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal("snapshot", message.Type);
            return Assert.Single(message.AppHosts!);
        }

        async IAsyncEnumerable<ResourceSnapshot> Watch(bool includeHidden, [EnumeratorCancellation] CancellationToken token)
        {
            Assert.True(includeHidden);
            Interlocked.Increment(ref watchCalls);
            try
            {
                await foreach (var resource in resourceUpdates.Reader.ReadAllAsync(token))
                {
                    yield return resource;
                }
            }
            finally
            {
                watchStopped.TrySetResult();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrFailedResourceStreamLeavesHostUnknownWithoutFailingDiscovery(bool fails)
    {
        using var cancellation = new CancellationTokenSource();
        var resourceUpdates = Channel.CreateUnbounded<ResourceSnapshot>();
        var messages = Channel.CreateUnbounded<TrayWatchMessage>();
        var connection = Connection("/project/a.cs", 10);
        connection.ResourceSnapshots = [new() { Name = "api", State = "Running", HealthStatus = "Healthy" }];
        connection.WatchResourceSnapshotsHandler = (_, token) => resourceUpdates.Reader.ReadAllAsync(token);
        var logger = new FakeLogger();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.SocketPath, connection);
        var run = new TrayWatchStream(monitor, new TestProcessIdentityProvider(), new FakeTimeProvider(), logger)
            .RunAsync((json, _) =>
            {
                messages.Writer.TryWrite(Deserialize(json));
                return Task.CompletedTask;
            }, cancellation.Token);

        try
        {
            Assert.Equal("healthy", Assert.Single((await messages.Reader.ReadAsync().AsTask().DefaultTimeout()).AppHosts!).Health);
            resourceUpdates.Writer.TryComplete(fails ? new IOException("Resource stream failed.") : null);

            var message = await messages.Reader.ReadAsync().AsTask().DefaultTimeout();
            Assert.Equal("snapshot", message.Type);
            Assert.Null(Assert.Single(message.AppHosts!).Health);
            var log = Assert.Single(logger.Collector.GetSnapshot());
            Assert.Equal(LogLevel.Debug, log.Level);
            Assert.Equal(fails
                ? "Resource health unavailable for AppHost PID 10."
                : "Resource health stream ended for AppHost PID 10.", log.Message);
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        }
    }

    [Fact]
    public async Task RemovedHostCancelsItsResourceWatchAndReplacementDoesNotReuseHealth()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshots = Channel.CreateUnbounded<IReadOnlyList<IAppHostAuxiliaryBackchannel>>();
        var messages = Channel.CreateUnbounded<TrayWatchMessage>();
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = Connection("/project/a.cs", 10);
        initial.ResourceSnapshots = [new() { Name = "api", State = "Running", HealthStatus = "Healthy" }];
        initial.WatchResourceSnapshotsHandler = Watch;
        var replacement = Connection("/project/a.cs", 10);
        replacement.ResourceSnapshots = [new() { Name = "api", State = "Waiting" }];
        var startedAt = 1000L;
        var monitor = new TestAuxiliaryBackchannelMonitor { WatchConnectionsHandler = token => snapshots.Reader.ReadAllAsync(token) };
        snapshots.Writer.TryWrite([initial]);
        var run = CreateStream(monitor, new FakeTimeProvider(), new TestProcessIdentityProvider { GetStartTime = _ => Volatile.Read(ref startedAt) })
            .RunAsync((json, _) =>
            {
                messages.Writer.TryWrite(Deserialize(json));
                return Task.CompletedTask;
            }, cancellation.Token);

        try
        {
            var first = Assert.Single((await messages.Reader.ReadAsync().AsTask().DefaultTimeout()).AppHosts!);
            Assert.Equal("healthy", first.Health);
            Assert.Equal(1000, first.ProcessStartTimeUnixMilliseconds);

            snapshots.Writer.TryWrite([]);
            Assert.Empty((await messages.Reader.ReadAsync().AsTask().DefaultTimeout()).AppHosts!);
            await watchStopped.Task.DefaultTimeout();

            Volatile.Write(ref startedAt, 2000);
            snapshots.Writer.TryWrite([replacement]);
            var second = Assert.Single((await messages.Reader.ReadAsync().AsTask().DefaultTimeout()).AppHosts!);
            Assert.Equal(first with { Health = "warning", ProcessStartTimeUnixMilliseconds = 2000 }, second);
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        }

        async IAsyncEnumerable<ResourceSnapshot> Watch(bool includeHidden, [EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                yield break;
            }
            finally
            {
                watchStopped.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task SharedMessagesUseCompactVersionedSchema()
    {
        var host = new TrayAppHost { AppHostPath = "/project/apphost.cs", AppHostPid = 42, ProcessStartTimeUnixMilliseconds = 1000 };
        var snapshot = new TrayWatchMessage
        {
            Version = TrayCliProtocol.Version,
            Type = "snapshot",
            AppHosts = [host]
        };
        var stop = new TrayStopMessage { Version = TrayCliProtocol.Version, Outcome = "stopped", ExitCode = 0 };
        await Verify(string.Join('\n',
            JsonSerializer.Serialize(snapshot, TrayCliJsonContext.Default.TrayWatchMessage),
            JsonSerializer.Serialize(snapshot with { AppHosts = [host with { Health = "healthy" }] }, TrayCliJsonContext.Default.TrayWatchMessage),
            JsonSerializer.Serialize(snapshot with { AppHosts = [host with { Health = "warning" }] }, TrayCliJsonContext.Default.TrayWatchMessage),
            JsonSerializer.Serialize(snapshot with { AppHosts = [host with { Health = "unhealthy" }] }, TrayCliJsonContext.Default.TrayWatchMessage),
            JsonSerializer.Serialize(stop, TrayCliJsonContext.Default.TrayStopMessage)), "txt");
    }

    private static TrayWatchStream CreateStream(TestAuxiliaryBackchannelMonitor monitor, TimeProvider time, TestProcessIdentityProvider? identity = null)
        => new(monitor, identity ?? new TestProcessIdentityProvider(), time, NullLogger.Instance);

    private static TrayWatchMessage Deserialize(string json)
        => JsonSerializer.Deserialize(json, TrayCliJsonContext.Default.TrayWatchMessage)!;

    private static TestAppHostAuxiliaryBackchannel Connection(string path, int pid) => new()
    {
        SocketPath = $"socket-{pid}",
        AppHostInfo = new AppHostInformation { AppHostPath = path, ProcessId = pid },
        WatchResourceSnapshotsHandler = (_, token) => WatchUntilCanceled(token)
    };

    private static async IAsyncEnumerable<ResourceSnapshot> WatchUntilCanceled([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }
}
