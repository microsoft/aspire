// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task SharedMessagesUseCompactVersionedSchema()
    {
        var snapshot = new TrayWatchMessage
        {
            Version = TrayCliProtocol.Version,
            Type = "snapshot",
            AppHosts = [new TrayAppHost { AppHostPath = "/project/apphost.cs", AppHostPid = 42, ProcessStartTimeUnixMilliseconds = 1000 }]
        };
        var stop = new TrayStopMessage { Version = TrayCliProtocol.Version, Outcome = "stopped", ExitCode = 0 };
        await Verify(string.Join('\n',
            JsonSerializer.Serialize(snapshot, TrayCliJsonContext.Default.TrayWatchMessage),
            JsonSerializer.Serialize(stop, TrayCliJsonContext.Default.TrayStopMessage)), "txt");
    }

    private static TrayWatchStream CreateStream(TestAuxiliaryBackchannelMonitor monitor, TimeProvider time, TestProcessIdentityProvider? identity = null)
        => new(monitor, identity ?? new TestProcessIdentityProvider(), time, NullLogger.Instance);

    private static TrayWatchMessage Deserialize(string json)
        => JsonSerializer.Deserialize(json, TrayCliJsonContext.Default.TrayWatchMessage)!;

    private static TestAppHostAuxiliaryBackchannel Connection(string path, int pid) => new()
    {
        SocketPath = $"socket-{pid}",
        AppHostInfo = new AppHostInformation { AppHostPath = path, ProcessId = pid }
    };
}
