// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Backchannel;

public class ResourceSnapshotWatcherTests
{
    [Fact]
    public async Task ResourceSnapshotWatcher_DisposeDuringInitialLoadCancelsGetAndWatch()
    {
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getCancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceSnapshotVersionsV1 = true,
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() => getCancellationRequested.TrySetResult());
                getStarted.TrySetResult();
                await getGate.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                WaitForResourceSnapshotGate(watchStarted, watchGate.Task, watchStopped, cancellationToken)
        };
        var watcher = new ResourceSnapshotWatcher(connection, NullLogger<ResourceSnapshotWatcher>.Instance);

        using (watcher)
        {
            await Task.WhenAll(getStarted.Task, watchStarted.Task).DefaultTimeout();
        }

        await Task.WhenAll(getCancellationRequested.Task, watchStopped.Task).DefaultTimeout();
        getGate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => watcher.WaitForInitialLoadAsync()).DefaultTimeout();
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_CancelsWatchWhenInitialLoadFails()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceSnapshotVersionsV1 = true,
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                WaitForResourceSnapshotCancellation(watchStarted, watchStopped, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection, NullLogger<ResourceSnapshotWatcher>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.True(watchStopped.Task.IsCompleted);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_PreservesInitialFailureWhenWatchCancellationCallbackThrows()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceSnapshotVersionsV1 = true,
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                WaitForCancellationWithThrowingCallback(watchStarted, watchStopped, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection, NullLogger<ResourceSnapshotWatcher>.Instance);

        var initialException = await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.Equal("Initial load failed.", initialException.Message);
        Assert.True(watchStopped.Task.IsCompleted);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_LoadsInitialSnapshotsBeforeWatchingWithoutVersionCapability()
    {
        var watchSnapshotApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = _ => Task.FromResult(
                new List<ResourceSnapshot>
                {
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Starting",
                        Version = 0
                    }
                }),
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                YieldSnapshotAndWait(
                    Task.CompletedTask,
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Running",
                        Version = 0
                    },
                    watchSnapshotApplied,
                    cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection, NullLogger<ResourceSnapshotWatcher>.Instance);

        await watcher.WaitForInitialLoadAsync().DefaultTimeout();
        await watchSnapshotApplied.Task.DefaultTimeout();

        var snapshot = Assert.Single(watcher.CaptureAllResources().Resources);
        Assert.Equal("Running", snapshot.State);
        Assert.Equal(0, snapshot.Version);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_PrefersNewerGetSnapshotOverReplayedWatchSnapshot()
    {
        var replayObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceSnapshotVersionsV1 = true,
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await replayObserved.Task.WaitAsync(cancellationToken);
                return
                [
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Running",
                        Version = 2
                    }
                ];
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                YieldSnapshotAndWait(
                    Task.CompletedTask,
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Starting",
                        Version = 1
                    },
                    replayObserved,
                    cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection, NullLogger<ResourceSnapshotWatcher>.Instance);

        await watcher.WaitForInitialLoadAsync().DefaultTimeout();

        var snapshot = Assert.Single(watcher.CaptureAllResources().Resources);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal("Running", snapshot.State);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_PrefersNewerWatchSnapshotOverStaleGetSnapshot()
    {
        var getCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceSnapshotVersionsV1 = true,
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                getCaptured.TrySetResult();
                await watchObserved.Task.WaitAsync(cancellationToken);
                return
                [
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Starting",
                        Version = 1
                    }
                ];
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                YieldSnapshotAndWait(
                    getCaptured.Task,
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Running",
                        Version = 2
                    },
                    watchObserved,
                    cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection, NullLogger<ResourceSnapshotWatcher>.Instance);

        await watcher.WaitForInitialLoadAsync().DefaultTimeout();

        var snapshot = Assert.Single(watcher.CaptureAllResources().Resources);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal("Running", snapshot.State);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_IgnoresStaleWatchSnapshotAfterInitialLoad()
    {
        var staleSnapshotGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleSnapshotProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerSnapshotGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerSnapshotProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new FakeLogger<ResourceSnapshotWatcher>();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceSnapshotVersionsV1 = true,
            GetResourceSnapshotsHandler = _ => Task.FromResult(
                new List<ResourceSnapshot>
                {
                    new()
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Running",
                        Version = 2
                    }
                }),
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                YieldSnapshotsInSequence(
                    staleSnapshotGate.Task,
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Starting",
                        Version = 1
                    },
                    staleSnapshotProcessed,
                    newerSnapshotGate.Task,
                    new ResourceSnapshot
                    {
                        Name = "api",
                        DisplayName = "api",
                        ResourceType = "Project",
                        State = "Finished",
                        Version = 3
                    },
                    newerSnapshotProcessed,
                    cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection, logger, bufferUpdates: true);
        await watcher.WaitForInitialLoadAsync().DefaultTimeout();
        var initialCapture = watcher.CaptureAllResources();
        await using var consumer = watcher
            .WatchResourceSnapshotBatchesAsync(initialCapture.UpdateSequence)
            .GetAsyncEnumerator();
        var moveNextTask = consumer.MoveNextAsync().AsTask();

        staleSnapshotGate.TrySetResult();
        await staleSnapshotProcessed.Task.DefaultTimeout();

        var staleCapture = watcher.CaptureAllResources();
        var retainedSnapshot = Assert.Single(staleCapture.Resources);
        Assert.Equal(2, retainedSnapshot.Version);
        Assert.Equal("Running", retainedSnapshot.State);
        Assert.Equal(initialCapture.UpdateSequence, staleCapture.UpdateSequence);
        Assert.Contains(logger.Collector.GetSnapshot(), record =>
            record.Level == LogLevel.Debug &&
            record.Message.Contains("api", StringComparison.Ordinal) &&
            record.Message.Contains('1') &&
            record.Message.Contains('2'));
        Assert.False(moveNextTask.IsCompleted);

        newerSnapshotGate.TrySetResult();
        await newerSnapshotProcessed.Task.DefaultTimeout();

        var currentSnapshot = Assert.Single(watcher.CaptureAllResources().Resources);
        Assert.Equal(3, currentSnapshot.Version);
        Assert.Equal("Finished", currentSnapshot.State);
        Assert.True(await moveNextTask.DefaultTimeout());
        Assert.False(consumer.Current.IsResync);
        var update = Assert.Single(consumer.Current.Snapshots);
        Assert.Equal(3, update.Version);
        Assert.Equal("Finished", update.State);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_AllowsOnlyOneUpdateConsumer()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = _ => Task.FromResult(new List<ResourceSnapshot>()),
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                WaitForResourceSnapshotCancellation(watchStarted, watchStopped, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(
            connection,
            NullLogger<ResourceSnapshotWatcher>.Instance,
            bufferUpdates: true);
        await watcher.WaitForInitialLoadAsync().DefaultTimeout();

        using var consumersCts = new CancellationTokenSource();
        await using var firstConsumer = watcher
            .WatchResourceSnapshotBatchesAsync(afterSequence: 0, consumersCts.Token)
            .GetAsyncEnumerator();
        var firstMoveNextTask = firstConsumer.MoveNextAsync().AsTask();
        Assert.False(firstMoveNextTask.IsCompleted);

        await using var secondConsumer = watcher
            .WatchResourceSnapshotBatchesAsync(afterSequence: 0, consumersCts.Token)
            .GetAsyncEnumerator();
        var secondMoveNextTask = secondConsumer.MoveNextAsync().AsTask();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => secondMoveNextTask).DefaultTimeout();

            Assert.Equal(
                "Resource snapshot updates support only one consumer for the lifetime of this watcher.",
                exception.Message);
        }
        finally
        {
            consumersCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstMoveNextTask).DefaultTimeout();

            try
            {
                await secondMoveNextTask.DefaultTimeout();
            }
            catch (OperationCanceledException) when (consumersCts.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_ResynchronizesAndCoalescesWithoutBlockingBackchannelUpdates()
    {
        var updatesGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int updatesPerResource = 3;
        var resourceCount = ResourceSnapshotWatcher.UpdateBufferCapacity + 1;
        var totalUpdateCount = resourceCount * updatesPerResource;
        var producedUpdateCount = 0;
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = _ => Task.FromResult(new List<ResourceSnapshot>()),
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                ProduceResourceSnapshotsAfter(
                    updatesGate.Task,
                    totalUpdateCount,
                    index =>
                    {
                        Interlocked.Increment(ref producedUpdateCount);
                        var resourceIndex = index / updatesPerResource;
                        var updateIndex = index % updatesPerResource;

                        return new ResourceSnapshot
                        {
                            Name = $"resource-{resourceIndex}",
                            DisplayName = $"resource-{resourceIndex}-update-{updateIndex}",
                            ResourceType = "Project",
                            State = $"State-{updateIndex}"
                        };
                    },
                    producerCompleted,
                    cancellationToken)
        };

        using var watcher = new ResourceSnapshotWatcher(
            connection,
            NullLogger<ResourceSnapshotWatcher>.Instance,
            bufferUpdates: true);
        await watcher.WaitForInitialLoadAsync().DefaultTimeout();
        var initialCapture = watcher.CaptureAllResources();

        updatesGate.TrySetResult();
        await producerCompleted.Task.DefaultTimeout();

        var batches = await watcher
            .WatchResourceSnapshotBatchesAsync(initialCapture.UpdateSequence)
            .ToListAsync()
            .DefaultTimeout();
        var batch = Assert.Single(batches);

        var expectedUpdates = Enumerable.Range(0, resourceCount)
            .Select(index => new
            {
                Name = $"resource-{index}",
                DisplayName = (string?)$"resource-{index}-update-{updatesPerResource - 1}",
                State = (string?)$"State-{updatesPerResource - 1}"
            })
            .OrderBy(update => update.Name, StringComparer.Ordinal)
            .ToList();
        var actualUpdates = batch.Snapshots
            .Select(update => new { update.Name, update.DisplayName, update.State })
            .OrderBy(update => update.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(totalUpdateCount, producedUpdateCount);
        Assert.True(batch.IsResync);
        Assert.Equal(resourceCount, batch.Snapshots.Count);
        Assert.Equal(expectedUpdates, actualUpdates);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_OverlapsProducerConsumerAndReaders()
    {
        var source = Channel.CreateUnbounded<ResourceSnapshot>();
        var firstSnapshotApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = _ => Task.FromResult(new List<ResourceSnapshot>()),
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                ReadSnapshotsAndSignalFirstApplied(source.Reader, firstSnapshotApplied, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(
            connection,
            NullLogger<ResourceSnapshotWatcher>.Instance,
            bufferUpdates: true);
        await watcher.WaitForInitialLoadAsync().DefaultTimeout();
        var initialCapture = watcher.CaptureAllResources();
        var resourceCount = ResourceSnapshotWatcher.UpdateBufferCapacity + 1;
        var observedVersions = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var producerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBatchConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSnapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSnapshot = new ResourceSnapshot
        {
            Name = "resource-0",
            DisplayName = "resource-0",
            ResourceType = "Project",
            State = "State-1",
            Version = 1
        };

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await source.Writer.WriteAsync(firstSnapshot);
                producerBlocked.TrySetResult();
                await releaseProducer.Task;

                for (var resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)
                {
                    for (var version = 1L; version <= 3; version++)
                    {
                        if (resourceIndex == 0 && version == 1)
                        {
                            continue;
                        }

                        await source.Writer.WriteAsync(new ResourceSnapshot
                        {
                            Name = $"resource-{resourceIndex}",
                            DisplayName = $"resource-{resourceIndex}",
                            ResourceType = "Project",
                            State = $"State-{version}",
                            Version = version
                        });
                        await Task.Yield();
                    }
                }
            }
            finally
            {
                source.Writer.TryComplete();
            }
        });
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var batch in watcher.WatchResourceSnapshotBatchesAsync(initialCapture.UpdateSequence))
            {
                foreach (var snapshot in batch.Snapshots)
                {
                    observedVersions.AddOrUpdate(
                        snapshot.Name,
                        snapshot.Version,
                        (_, currentVersion) => Math.Max(currentVersion, snapshot.Version));
                }

                if (batch.Snapshots.Contains(firstSnapshot))
                {
                    firstBatchConsumed.TrySetResult();
                }
            }
        });
        var readerTask = Task.Run(async () =>
        {
            await Task.WhenAll(producerBlocked.Task, firstSnapshotApplied.Task);

            Assert.Same(firstSnapshot, watcher.GetResource(firstSnapshot.Name));
            Assert.Equal([firstSnapshot], watcher.GetAllResources());
            Assert.Equal([firstSnapshot], watcher.CaptureAllResources().Resources);
            firstSnapshotRead.TrySetResult();
        });

        await producerBlocked.Task.DefaultTimeout();
        await Task.WhenAll(firstSnapshotRead.Task, firstBatchConsumed.Task).DefaultTimeout();
        Assert.False(producerTask.IsCompleted);
        releaseProducer.TrySetResult();

        await Task.WhenAll(producerTask, consumerTask, readerTask).DefaultTimeout();

        var finalCapture = watcher.CaptureAllResources();
        Assert.Equal(resourceCount, finalCapture.Resources.Count);
        Assert.All(finalCapture.Resources, snapshot => Assert.Equal(3, snapshot.Version));
        Assert.Equal(resourceCount, observedVersions.Count);
        for (var resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)
        {
            Assert.True(observedVersions.TryGetValue($"resource-{resourceIndex}", out var version));
            Assert.Equal(3, version);
        }
    }

    private static async IAsyncEnumerable<ResourceSnapshot> ReadSnapshotsAndSignalFirstApplied(
        ChannelReader<ResourceSnapshot> source,
        TaskCompletionSource firstSnapshotApplied,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var firstSnapshot = true;
        await foreach (var snapshot in source.ReadAllAsync(cancellationToken))
        {
            yield return snapshot;

            if (firstSnapshot)
            {
                // Code after the yield runs only when the watcher asks for the next item, proving
                // that the first snapshot passed through WatchChangesAsync before readers inspect it.
                firstSnapshotApplied.TrySetResult();
                firstSnapshot = false;
            }
        }
    }

    private static async IAsyncEnumerable<ResourceSnapshot> YieldSnapshotAndWait(
        Task prerequisite,
        ResourceSnapshot snapshot,
        TaskCompletionSource snapshotObserved,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await prerequisite.WaitAsync(cancellationToken);
        yield return snapshot;

        // Code after the yield runs only when the watcher asks for the next item, which means
        // the yielded snapshot has already been applied to the watcher's resource dictionary.
        snapshotObserved.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ResourceSnapshot> YieldSnapshotsInSequence(
        Task firstGate,
        ResourceSnapshot firstSnapshot,
        TaskCompletionSource firstSnapshotProcessed,
        Task secondGate,
        ResourceSnapshot secondSnapshot,
        TaskCompletionSource secondSnapshotProcessed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await firstGate.WaitAsync(cancellationToken);
        yield return firstSnapshot;

        // Code after each yield runs only after the watcher requests the next item, proving the
        // previous snapshot passed through WatchChangesAsync before the test inspects its effects.
        firstSnapshotProcessed.TrySetResult();
        await secondGate.WaitAsync(cancellationToken);
        yield return secondSnapshot;
        secondSnapshotProcessed.TrySetResult();
    }

    private static async IAsyncEnumerable<ResourceSnapshot> ProduceResourceSnapshotsAfter(
        Task prerequisite,
        int count,
        Func<int, ResourceSnapshot> createSnapshot,
        TaskCompletionSource producerCompleted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await prerequisite.WaitAsync(cancellationToken);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return createSnapshot(i);
            await Task.Yield();
        }

        producerCompleted.TrySetResult();
    }

    private static async IAsyncEnumerable<ResourceSnapshot> WaitForResourceSnapshotCancellation(
        TaskCompletionSource watchStarted,
        TaskCompletionSource watchStopped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        watchStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            watchStopped.TrySetResult();
        }

        yield break;
    }

    private static async IAsyncEnumerable<ResourceSnapshot> WaitForResourceSnapshotGate(
        TaskCompletionSource watchStarted,
        Task gate,
        TaskCompletionSource watchStopped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        watchStarted.TrySetResult();
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        finally
        {
            watchStopped.TrySetResult();
        }

        yield break;
    }

    private static async IAsyncEnumerable<ResourceSnapshot> WaitForCancellationWithThrowingCallback(
        TaskCompletionSource watchStarted,
        TaskCompletionSource watchStopped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            () => throw new InvalidOperationException("Cancellation callback failed."));
        watchStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            watchStopped.TrySetResult();
        }

        yield break;
    }
}
