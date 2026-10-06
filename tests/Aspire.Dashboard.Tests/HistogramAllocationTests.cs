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
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            point.TimeUnixNano += 1_000;
            current = HistogramValue.Create(point, OtlpAggregationTemporality.Cumulative, previous);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Same(previous, current);
        Assert.Equal(OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano), previous.End);
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
        var before = GC.GetAllocatedBytesForCurrentThread();
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

        var before = GC.GetAllocatedBytesForCurrentThread();
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
        var before = GC.GetAllocatedBytesForCurrentThread();
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
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            added = HistogramBuckets.AddBounds(ref bounds, incoming, incomingBounds, previous);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(added);
        Assert.Same(originalBounds, bounds);
    }
}
