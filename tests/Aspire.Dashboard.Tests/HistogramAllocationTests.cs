// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Tests.Shared.Telemetry;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class HistogramAllocationTests
{
    private static readonly DateTime s_start = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_UnchangedCumulativeSnapshot_DoesNotAllocate()
    {
        var point = HistogramTestHelpers.CreatePoint(s_start, s_start.AddSeconds(1), [1, 2, 3, 4], [10, 50, 100]);
        var previous = HistogramValue.Create(point, OtlpAggregationTemporality.Cumulative, previous: null);

        // Warm the synchronous path before measuring so one-time runtime initialization is excluded.
        for (var i = 0; i < 100; i++)
        {
            HistogramValue.Create(point, OtlpAggregationTemporality.Cumulative, previous);
        }

        var current = previous;
        var before = GetAllocatedBytesWithEmptyAllocationContext();
        for (var i = 0; i < 1_000; i++)
        {
            point.TimeUnixNano += 1_000;
            current = HistogramValue.Create(point, OtlpAggregationTemporality.Cumulative, previous);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Same(previous, current);
        Assert.Equal(OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano), previous.End);
        Assert.Equal(point.TimeUnixNano, previous.EndTimeUnixNano);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_MatchingBounds_DoesNotAllocate(bool cumulative)
    {
        ulong[] originalCounts = [0, 0, 0];
        double[] originalBounds = [10, 100];
        ulong[] incoming = [2, 4, 6];
        ulong[] previous = cumulative ? [1, 2, 3] : [];
        ulong[]? counts = originalCounts;
        double[]? bounds = originalBounds;
        for (var i = 0; i < 100; i++)
        {
            HistogramBuckets.Add(ref counts, ref bounds, incoming, originalBounds, previous);
        }

        var added = false;
        var before = GetAllocatedBytesWithEmptyAllocationContext();
        for (var i = 0; i < 1_000; i++)
        {
            added = HistogramBuckets.Add(ref counts, ref bounds, incoming, originalBounds, previous);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(added);
        Assert.Same(originalCounts, counts);
        Assert.Same(originalBounds, bounds);
        Assert.Equal(cumulative ? [1_100ul, 2_200ul, 3_300ul] : [2_200ul, 4_400ul, 6_600ul], originalCounts);
    }

    [Fact]
    public void Add_FinerBoundsIntoCoarserAccumulator_DoesNotAllocate()
    {
        ulong[] originalCounts = [0, 0, 0];
        double[] originalBounds = [10, 100];
        ulong[] incoming = [1, 2, 3, 4];
        double[] incomingBounds = [10, 50, 100];
        ulong[]? counts = originalCounts;
        double[]? bounds = originalBounds;
        for (var i = 0; i < 100; i++)
        {
            HistogramBuckets.Add(ref counts, ref bounds, incoming, incomingBounds, []);
        }

        var before = GetAllocatedBytesWithEmptyAllocationContext();
        for (var i = 0; i < 1_000; i++)
        {
            HistogramBuckets.Add(ref counts, ref bounds, incoming, incomingBounds, []);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Same(originalCounts, counts);
        Assert.Same(originalBounds, bounds);
        Assert.Equal([1_100ul, 5_500ul, 4_400ul], originalCounts);
    }

    [Fact]
    public void Add_UnchangedBuckets_DoesNotAllocateAccumulator()
    {
        ulong[] incoming = [1, 2, 3];
        double[] incomingBounds = [10, 100];
        ulong[]? counts = null;
        double[]? bounds = null;
        for (var i = 0; i < 100; i++)
        {
            HistogramBuckets.Add(ref counts, ref bounds, incoming, incomingBounds, incoming);
        }

        var added = true;
        var before = GetAllocatedBytesWithEmptyAllocationContext();
        for (var i = 0; i < 1_000; i++)
        {
            added = HistogramBuckets.Add(ref counts, ref bounds, incoming, incomingBounds, incoming);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.False(added);
        Assert.Null(counts);
        Assert.Null(bounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddBounds_CompatibleLayouts_DoesNotAllocate(bool cumulative)
    {
        double[] originalBounds = [10, 100];
        double[] incomingBounds = [10, 50, 100];
        ulong[] incoming = [2, 4, 6, 8];
        ulong[] previous = cumulative ? [1, 2, 3, 4] : [];
        double[]? bounds = originalBounds;
        for (var i = 0; i < 100; i++)
        {
            HistogramBuckets.AddBounds(ref bounds, incoming, incomingBounds, previous);
        }

        var added = false;
        var before = GetAllocatedBytesWithEmptyAllocationContext();
        for (var i = 0; i < 1_000; i++)
        {
            added = HistogramBuckets.AddBounds(ref bounds, incoming, incomingBounds, previous);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(added);
        Assert.Same(originalBounds, bounds);
    }

    [Fact]
    public void AllocationMeasurement_IgnoresAllocationContextVoidedByBackgroundGC()
    {
        // Starts a background GC, gives this thread a partially used allocation context while
        // the GC is marking, and then waits inside a non-allocating window for the background
        // GC to void that context. Without emptying the context first, nearly every attempt
        // reports the unused remainder (up to one allocation quantum) as allocated.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var collector = new BlockingCollectionWorker();
            collector.Start();

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
            GC.KeepAlive(new object());

            var before = GetAllocatedBytesWithEmptyAllocationContext();
            // A blocking full GC cannot start until the background GC, including the end of
            // its mark phase, has finished.
            collector.CollectAndWait();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            collector.Join();
            Assert.Equal(0, allocated);
        }
    }

    // A background GC voids every thread's allocation context at the end of its mark phase
    // without crediting the unused remainder back to that thread, so a thread holding a partially
    // used context observes up to one allocation quantum of "allocation" it never made. Every GC
    // starts by retiring allocation contexts with correct accounting, so after a GC this thread has
    // no context left to void until it allocates again.
    private static long GetAllocatedBytesWithEmptyAllocationContext()
    {
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        return GC.GetAllocatedBytesForCurrentThread();
    }

    private sealed class BlockingCollectionWorker
    {
        private readonly Thread _thread;
        private volatile bool _collect;
        private volatile bool _collected;

        public BlockingCollectionWorker()
        {
            _thread = new Thread(Run) { IsBackground = true };
        }

        public void Start() => _thread.Start();

        // Spins rather than blocking on a wait handle so the waiting thread does not allocate.
        public void CollectAndWait()
        {
            _collect = true;
            while (!_collected)
            {
                Thread.SpinWait(1);
            }
        }

        public void Join() => _thread.Join();

        private void Run()
        {
            while (!_collect)
            {
                Thread.SpinWait(1);
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            _collected = true;
        }
    }
}
