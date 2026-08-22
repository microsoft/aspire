// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Backchannel;

public class ResourceSnapshotWatcherTests
{
    [Fact]
    public async Task ResourceSnapshotWatcher_DisposeDuringInitialLoadDoesNotRecordWatchException()
    {
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getCancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
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
        var watcher = new ResourceSnapshotWatcher(connection);

        using (watcher)
        {
            await Task.WhenAll(getStarted.Task, watchStarted.Task).DefaultTimeout();
        }

        await Task.WhenAll(getCancellationRequested.Task, watchStopped.Task).DefaultTimeout();
        getGate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => watcher.WaitForInitialLoadAsync()).DefaultTimeout();
        Assert.Null(watcher.WatchException);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_CancelsWatchWhenInitialLoadFails()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                WaitForResourceSnapshotCancellation(watchStarted, watchStopped, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.True(watchStopped.Task.IsCompleted);
        Assert.Null(watcher.WatchException);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_RetainsIndependentWatchCancellationWhenInitialLoadFails()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                ThrowUnrelatedCancellationAfterWatchCancellation(watchStarted, unrelatedCts.Token, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        var watchException = Assert.IsType<OperationCanceledException>(watcher.WatchException);
        Assert.Equal(unrelatedCts.Token, watchException.CancellationToken);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_RetainsTokenlessWatchFailureThatCompletedBeforeInitialLoadFails()
    {
        var expectedWatchException = new OperationCanceledException();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = _ => Task.FromException<List<ResourceSnapshot>>(
                new InvalidOperationException("Initial load failed.")),
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                ThrowTokenlessCancellationImmediately(expectedWatchException, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection);

        var initialException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.Equal("Initial load failed.", initialException.Message);
        Assert.Same(expectedWatchException, watcher.WatchException);
        Assert.Equal(default, expectedWatchException.CancellationToken);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_IgnoresTokenlessWatchCancellationWhenInitialLoadFails()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                ThrowTokenlessCancellationAfterWatchCancellation(watchStarted, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.Null(watcher.WatchException);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_PreservesInitialFailureWhenWatchCancellationCallbackThrows()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                WaitForCancellationWithThrowingCallback(watchStarted, watchStopped, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection);

        var initialException = await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.Equal("Initial load failed.", initialException.Message);
        Assert.True(watchStopped.Task.IsCompleted);
        Assert.IsType<AggregateException>(watcher.WatchException);
    }

    [Fact]
    public async Task ResourceSnapshotWatcher_PrefersWatchFailureOverCancellationCallbackFailure()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                await watchStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Initial load failed.");
            },
            WatchResourceSnapshotsHandler = (_, cancellationToken) =>
                ThrowWatchFailureWithThrowingCancellationCallback(watchStarted, cancellationToken)
        };
        using var watcher = new ResourceSnapshotWatcher(connection);

        var initialException = await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.WaitForInitialLoadAsync()).DefaultTimeout();

        Assert.Equal("Initial load failed.", initialException.Message);
        var watchException = Assert.IsType<IOException>(watcher.WatchException);
        Assert.Equal("Watch failed.", watchException.Message);
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
        using var watcher = new ResourceSnapshotWatcher(connection, bufferUpdates: true);
        await watcher.WaitForInitialLoadAsync().DefaultTimeout();

        using var consumersCts = new CancellationTokenSource();
        await using var firstConsumer = watcher
            .WatchResourceSnapshotsAsync(afterSequence: 0, consumersCts.Token)
            .GetAsyncEnumerator();
        var firstMoveNextTask = firstConsumer.MoveNextAsync().AsTask();
        Assert.False(firstMoveNextTask.IsCompleted);

        await using var secondConsumer = watcher
            .WatchResourceSnapshotsAsync(afterSequence: 0, consumersCts.Token)
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

        using var watcher = new ResourceSnapshotWatcher(connection, bufferUpdates: true);
        await watcher.WaitForInitialLoadAsync().DefaultTimeout();
        var initialCapture = watcher.CaptureAllResources();

        updatesGate.TrySetResult();
        await producerCompleted.Task.DefaultTimeout();

        var updates = await watcher
            .WatchResourceSnapshotsAsync(initialCapture.UpdateSequence)
            .ToListAsync()
            .DefaultTimeout();

        var expectedUpdates = Enumerable.Range(0, resourceCount)
            .Select(index => new
            {
                Name = $"resource-{index}",
                DisplayName = (string?)$"resource-{index}-update-{updatesPerResource - 1}",
                State = (string?)$"State-{updatesPerResource - 1}"
            })
            .OrderBy(update => update.Name, StringComparer.Ordinal)
            .ToList();
        var actualUpdates = updates
            .Select(update => new { update.Name, update.DisplayName, update.State })
            .OrderBy(update => update.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(totalUpdateCount, producedUpdateCount);
        Assert.Equal(resourceCount, updates.Count);
        Assert.Equal(expectedUpdates, actualUpdates);
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

    private static async IAsyncEnumerable<ResourceSnapshot> ThrowUnrelatedCancellationAfterWatchCancellation(
        TaskCompletionSource watchStarted,
        CancellationToken unrelatedCancellationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        watchStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(unrelatedCancellationToken);
        }

        yield break;
    }

    private static async IAsyncEnumerable<ResourceSnapshot> ThrowTokenlessCancellationImmediately(
        OperationCanceledException exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (!cancellationToken.IsCancellationRequested)
        {
            throw exception;
        }

        yield break;
    }

    private static async IAsyncEnumerable<ResourceSnapshot> ThrowTokenlessCancellationAfterWatchCancellation(
        TaskCompletionSource watchStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        watchStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
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

    private static async IAsyncEnumerable<ResourceSnapshot> ThrowWatchFailureWithThrowingCancellationCallback(
        TaskCompletionSource watchStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            () => throw new InvalidOperationException("Cancellation callback failed."));
        watchStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Watch failed.");
        }

        yield break;
    }
}
