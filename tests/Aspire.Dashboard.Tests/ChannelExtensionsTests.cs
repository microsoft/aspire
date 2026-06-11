// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class ChannelExtensionsTests
{
    [Fact]
    public async Task GetBatchesAsync_AsyncEnumerable_YieldsFullAndFinalBatches()
    {
        var batches = new List<int[]>();

        await foreach (var batch in EnumerateAsync([1, 2, 3, 4, 5]).GetBatchesAsync(maxBatchSize: 2))
        {
            batches.Add(batch);
        }

        Assert.Collection(
            batches,
            b => Assert.Equal([1, 2], b),
            b => Assert.Equal([3, 4], b),
            b => Assert.Equal([5], b));
    }

    [Fact]
    public async Task GetBatchesAsync_AsyncEnumerable_CancellationToken_Exits()
    {
        var cts = new CancellationTokenSource();
        var batches = new List<int[]>();

        var readTask = Task.Run(async () =>
        {
            await foreach (var batch in EnumerateUntilCancelledAsync(cts.Token).GetBatchesAsync(maxBatchSize: 1, cts.Token))
            {
                batches.Add(batch);
                cts.Cancel();
            }
        });

        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();

        var batch = Assert.Single(batches);
        Assert.Equal([1], batch);
    }

    [Fact]
    public async Task GetBatchesAsync_CancellationToken_Exits()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<IReadOnlyList<string>>();

        channel.Writer.TryWrite(["a", "b", "c"]);

        // Act
        IReadOnlyList<IReadOnlyList<string>>? readBatch = null;
        var readTask = Task.Run(async () =>
        {
            await foreach (var batch in channel.GetBatchesAsync(cancellationToken: cts.Token))
            {
                readBatch = batch;
                cts.Cancel();
            }
        });

        // Assert
        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();
    }

    [Fact]
    public async Task GetBatchesAsync_WithCancellation_Exits()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<IReadOnlyList<string>>();

        channel.Writer.TryWrite(["a", "b", "c"]);

        // Act
        IReadOnlyList<IReadOnlyList<string>>? readBatch = null;
        var readTask = Task.Run(async () =>
        {
            await foreach (var batch in channel.GetBatchesAsync().WithCancellation(cts.Token))
            {
                readBatch = batch;
                cts.Cancel();
            }
        });

        // Assert
        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();
    }

    [Fact]
    public async Task GetBatchesAsync_MinReadInterval_WaitForNextRead()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<IReadOnlyList<string>>();
        var resultChannel = Channel.CreateUnbounded<IReadOnlyList<IReadOnlyList<string>>>();
        var minReadInterval = TimeSpan.FromMilliseconds(500);

        channel.Writer.TryWrite(["a", "b", "c"]);

        // Act
        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in channel.GetBatchesAsync(minReadInterval).WithCancellation(cts.Token))
                {
                    resultChannel.Writer.TryWrite(batch);
                }
            }
            finally
            {
                resultChannel.Writer.Complete();
            }
        });

        // Assert
        var stopwatch = Stopwatch.StartNew();
        var read1 = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(["a", "b", "c"], read1.Single());

        channel.Writer.TryWrite(["d", "e", "f"]);

        var read2 = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(["d", "e", "f"], read2.Single());

        var elapsed = stopwatch.Elapsed;
        CustomAssert.AssertExceedsMinInterval(elapsed, minReadInterval);

        channel.Writer.Complete();
        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();
    }

    [Fact]
    public async Task GetBatchesAsync_MinReadInterval_WithCancellation_Exit()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<IReadOnlyList<string>>();
        var resultChannel = Channel.CreateUnbounded<IReadOnlyList<IReadOnlyList<string>>>();
        var minReadInterval = TimeSpan.FromMilliseconds(50000);

        channel.Writer.TryWrite(["a", "b", "c"]);

        // Act
        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in channel.GetBatchesAsync(minReadInterval).WithCancellation(cts.Token))
                {
                    resultChannel.Writer.TryWrite(batch);
                }
            }
            finally
            {
                resultChannel.Writer.Complete();
            }
        });

        // Assert
        var stopwatch = Stopwatch.StartNew();
        var read1 = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(["a", "b", "c"], read1.Single());

        channel.Writer.TryWrite(["d", "e", "f"]);

        var read2Task = resultChannel.Reader.ReadAsync().DefaultTimeout();
        cts.Cancel();

        await TaskHelpers.WaitIgnoreCancelAsync(readTask).DefaultTimeout();
        try
        {
            await read2Task.DefaultTimeout();
        }
        catch (ChannelClosedException)
        {
        }

        var elapsed = stopwatch.Elapsed;
        Assert.True(elapsed <= minReadInterval, $"Elapsed time {elapsed} should be less than min read interval {minReadInterval} on cancellation.");
    }

    private static async IAsyncEnumerable<int> EnumerateAsync(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return value;
        }
    }

    private static async IAsyncEnumerable<int> EnumerateUntilCancelledAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
