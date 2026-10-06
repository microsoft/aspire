// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Components;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class ChartDataCalculatorTests
{
    private static readonly DateTimeOffset s_startTime = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static OtlpContext CreateContext() =>
        new() { Options = new TelemetryLimitOptions(), Logger = NullLogger.Instance };

    // Identity function for time conversion — tests use UTC directly.
    private static DateTimeOffset ToLocal(DateTimeOffset dt) => dt;

    [Fact]
    public void CalcOffset_ReturnsCorrectOffset()
    {
        var now = s_startTime;
        var duration = TimeSpan.FromSeconds(10);

        Assert.Equal(now, ChartDataCalculator.CalcOffset(0, now, duration));
        Assert.Equal(now.Subtract(TimeSpan.FromSeconds(10)), ChartDataCalculator.CalcOffset(1, now, duration));
        Assert.Equal(now.Subtract(TimeSpan.FromSeconds(30)), ChartDataCalculator.CalcOffset(3, now, duration));
        Assert.Equal(now.Add(TimeSpan.FromSeconds(10)), ChartDataCalculator.CalcOffset(-1, now, duration));
    }

    [Theory]
    [InlineData(50, 10.0)]
    [InlineData(90, 50.0)]
    [InlineData(99, 100.0)]
    public void CalculatePercentile_ReturnsExpectedBucket(int percentile, double expected)
    {
        // Buckets: [0, 10) = 50 items, [10, 50) = 40 items, [50, 100) = 10 items
        ulong[] counts = [50, 40, 10];
        double[] bounds = [10.0, 50.0, 100.0];

        var result = ChartDataCalculator.CalculatePercentile(percentile, counts, bounds);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculatePercentile_AllInOneBucket_ReturnsThatBound()
    {
        ulong[] counts = [0, 0, 100];
        double[] bounds = [10.0, 50.0, 100.0];

        Assert.Equal(100.0, ChartDataCalculator.CalculatePercentile(50, counts, bounds));
        Assert.Equal(100.0, ChartDataCalculator.CalculatePercentile(99, counts, bounds));
    }

    [Fact]
    public void CalculatePercentile_InvalidPercentile_Throws()
    {
        ulong[] counts = [10];
        double[] bounds = [1.0];

        Assert.Throws<ArgumentOutOfRangeException>(() => ChartDataCalculator.CalculatePercentile(-1, counts, bounds));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartDataCalculator.CalculatePercentile(101, counts, bounds));
    }

    [Fact]
    public void CalculatePercentile_OverflowBucket_ReturnsLastBound()
    {
        // Most data is in the overflow (+Inf) bucket beyond the last explicit bound.
        // counts has explicitBounds.Length + 1 entries; the last entry is the overflow bucket.
        ulong[] counts = [0, 0, 5, 95];
        double[] bounds = [10.0, 50.0, 100.0];

        // P50 is in the overflow bucket — best estimate is the last finite bound.
        Assert.Equal(100.0, ChartDataCalculator.CalculatePercentile(50, counts, bounds));
        Assert.Equal(100.0, ChartDataCalculator.CalculatePercentile(99, counts, bounds));
    }

    [Fact]
    public void CalculatePercentile_ZeroTotalCount_ReturnsNull()
    {
        ulong[] counts = [0, 0, 0];
        double[] bounds = [10.0, 50.0];

        Assert.Null(ChartDataCalculator.CalculatePercentile(50, counts, bounds));
    }

    [Fact]
    public void TryCalculatePoint_MetricInRange_ReturnsValue()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);
        var start = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2025, 6, 15, 12, 0, 10, TimeSpan.Zero);

        // Add a metric value with timestamps within [start, end].
        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 42,
            StartTimeUnixNano = ToNanos(start),
            TimeUnixNano = ToNanos(end)
        }, context);

        var result = ChartDataCalculator.TryCalculatePoint([dimension], start, end, out var pointValue);

        Assert.True(result);
        Assert.Equal(42, pointValue);
    }

    [Fact]
    public void TryCalculatePoint_MetricOutOfRange_ReturnsFalse()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);
        var metricStart = new DateTimeOffset(2025, 6, 15, 11, 0, 0, TimeSpan.Zero);
        var metricEnd = new DateTimeOffset(2025, 6, 15, 11, 0, 10, TimeSpan.Zero);

        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 42,
            StartTimeUnixNano = ToNanos(metricStart),
            TimeUnixNano = ToNanos(metricEnd)
        }, context);

        // Query a time range that doesn't overlap the metric.
        var queryStart = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var queryEnd = new DateTimeOffset(2025, 6, 15, 12, 0, 10, TimeSpan.Zero);

        var result = ChartDataCalculator.TryCalculatePoint([dimension], queryStart, queryEnd, out var pointValue);

        Assert.False(result);
        Assert.Equal(0, pointValue);
    }

    [Fact]
    public void TryCalculatePoint_MultipleDimensions_SumsValues()
    {
        var context = CreateContext();
        var dim1 = new DimensionScope(capacity: 100, []);
        var dim2 = new DimensionScope(capacity: 100, []);
        var start = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2025, 6, 15, 12, 0, 10, TimeSpan.Zero);

        dim1.AddPointValue(new NumberDataPoint
        {
            AsInt = 10,
            StartTimeUnixNano = ToNanos(start),
            TimeUnixNano = ToNanos(end)
        }, context);

        dim2.AddPointValue(new NumberDataPoint
        {
            AsInt = 20,
            StartTimeUnixNano = ToNanos(start),
            TimeUnixNano = ToNanos(end)
        }, context);

        var result = ChartDataCalculator.TryCalculatePoint([dim1, dim2], start, end, out var pointValue);

        Assert.True(result);
        Assert.Equal(30, pointValue);
    }

    [Fact]
    public void TryCalculatePoint_StaggeredDimensionChanges_SumsCurrentValues()
    {
        var context = CreateContext();
        var stableDimension = new DimensionScope(capacity: 100, []);
        var changingDimension = new DimensionScope(capacity: 100, []);
        var start = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        for (var minute = 1; minute <= 3; minute++)
        {
            stableDimension.AddPointValue(new NumberDataPoint
            {
                AsInt = 10,
                StartTimeUnixNano = ToNanos(start),
                TimeUnixNano = ToNanos(start.AddMinutes(minute))
            }, context);
            changingDimension.AddPointValue(new NumberDataPoint
            {
                AsInt = 15 + (minute * 5),
                StartTimeUnixNano = ToNanos(start),
                TimeUnixNano = ToNanos(start.AddMinutes(minute))
            }, context);
        }

        var result = ChartDataCalculator.TryCalculatePoint(
            [stableDimension, changingDimension],
            start.AddMinutes(3).AddSeconds(-1),
            start.AddMinutes(3),
            out var pointValue);

        Assert.True(result);
        Assert.Equal(40, pointValue);
    }

    [Fact]
    public void TryCalculatePoint_MultipleMetricsInDimension_TakesMax()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);
        var start = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2025, 6, 15, 12, 0, 10, TimeSpan.Zero);

        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 5,
            StartTimeUnixNano = ToNanos(start),
            TimeUnixNano = ToNanos(start.AddSeconds(5))
        }, context);

        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 15,
            StartTimeUnixNano = ToNanos(start.AddSeconds(5)),
            TimeUnixNano = ToNanos(end)
        }, context);

        var result = ChartDataCalculator.TryCalculatePoint([dimension], start, end, out var pointValue);

        Assert.True(result);
        Assert.Equal(15, pointValue);
    }

    [Fact]
    public void CalculateChartValues_ProducesCorrectStructure()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);

        // Add a metric covering the entire time range so at least one bucket has data.
        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 100,
            StartTimeUnixNano = 0,
            TimeUnixNano = long.MaxValue
        }, context);

        var calculator = new ChartDataCalculator(pointCount: 30, duration: TimeSpan.FromMinutes(5));
        var data = calculator.CalculateChartValues([dimension], s_startTime, ToLocal, "Bytes");

        // 30 points + 2 extra = 32 x-values
        Assert.Equal(32, data.XValues.Count);
        Assert.Single(data.Traces);

        var trace = data.Traces[0];
        Assert.Equal("Bytes", trace.Name);
        Assert.Equal(32, trace.Values.Count);
        Assert.Equal(32, trace.DiffValues.Count);

        // No tooltips are generated by the calculator.
        Assert.Empty(trace.Tooltips);

        // Non-histogram charts produce no exemplars.
        Assert.Empty(data.Exemplars);
    }

    [Fact]
    public void CalculateChartValues_XValuesInChronologicalOrder()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);
        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 1,
            StartTimeUnixNano = 0,
            TimeUnixNano = long.MaxValue
        }, context);

        var calculator = new ChartDataCalculator(pointCount: 10, duration: TimeSpan.FromSeconds(100));
        var data = calculator.CalculateChartValues([dimension], s_startTime, ToLocal, "Count");

        for (var i = 1; i < data.XValues.Count; i++)
        {
            Assert.True(data.XValues[i] > data.XValues[i - 1],
                $"xValues[{i}] ({data.XValues[i]}) should be after xValues[{i - 1}] ({data.XValues[i - 1]})");
        }
    }

    [Fact]
    public void CalculateChartValues_NoDimensions_AllNullValues()
    {
        var calculator = new ChartDataCalculator(pointCount: 5, duration: TimeSpan.FromSeconds(50));
        var data = calculator.CalculateChartValues([], s_startTime, ToLocal, "Count");

        Assert.Equal(7, data.XValues.Count); // 5 + 2
        Assert.All(data.Traces[0].Values, v => Assert.Null(v));
    }

    [Fact]
    public void CalculateHistogramValues_ProducesThreePercentileTraces()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);

        // Add a histogram value that covers the time range.
        var histogramPoint = new HistogramDataPoint
        {
            StartTimeUnixNano = 0,
            TimeUnixNano = (ulong)s_startTime.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000,
            Count = 100,
            Sum = 500.0,
        };
        histogramPoint.ExplicitBounds.AddRange([10.0, 50.0, 100.0]);
        histogramPoint.BucketCounts.AddRange([50UL, 40UL, 10UL, 0UL]);

        dimension.AddHistogramValue(histogramPoint, OtlpAggregationTemporality.Cumulative, context);

        var calculator = new ChartDataCalculator(pointCount: 5, duration: TimeSpan.FromSeconds(50));
        var data = calculator.CalculateHistogramValues([dimension], s_startTime, ToLocal, "ms");

        Assert.Equal(3, data.Traces.Count);
        Assert.Equal(50, data.Traces[0].Percentile);
        Assert.Equal(90, data.Traces[1].Percentile);
        Assert.Equal(99, data.Traces[2].Percentile);
        Assert.Equal("P50 ms", data.Traces[0].Name);
        Assert.Equal("P90 ms", data.Traces[1].Name);
        Assert.Equal("P99 ms", data.Traces[2].Name);

        // Each trace should have 7 values (5 + 2 extra points).
        Assert.All(data.Traces, t => Assert.Equal(7, t.Values.Count));
        Assert.All(data.Traces, t => Assert.Equal(7, t.DiffValues.Count));

        // No tooltips are generated by the calculator.
        Assert.All(data.Traces, t => Assert.Empty(t.Tooltips));
    }

    [Fact]
    public void CalculateHistogramValues_NoDimensions_AllNullValuesAndDifferences()
    {
        var calculator = new ChartDataCalculator(pointCount: 5, duration: TimeSpan.FromSeconds(10));
        var data = calculator.CalculateHistogramValues([], s_startTime, ToLocal, "ms");

        Assert.False(data.HasIncompatibleHistogramBounds);
        Assert.Equal(3, data.Traces.Count);
        Assert.All(data.Traces, trace =>
        {
            Assert.Equal([null, null, null, null, null, null, null], trace.Values);
            Assert.Equal(trace.Values, trace.DiffValues);
        });
    }

    [Theory]
    [InlineData(OtlpAggregationTemporality.Cumulative, false)]
    [InlineData(OtlpAggregationTemporality.Cumulative, true)]
    [InlineData(OtlpAggregationTemporality.Delta, false)]
    [InlineData(OtlpAggregationTemporality.Delta, true)]
    public void CalculateHistogramValues_StackedDifferences_PreservesMissingIntervals(OtlpAggregationTemporality temporality, bool sameBucket)
    {
        var dimension = new DimensionScope(100, []);
        var time = s_startTime.UtcDateTime.AddMilliseconds(-500);
        dimension.AddHistogramValue(
            HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), sameBucket ? [100, 0, 0, 0] : [50, 40, 10, 0], [10, 50, 100]),
            temporality, CreateContext());
        var calculator = new ChartDataCalculator(pointCount: 5, duration: TimeSpan.FromSeconds(10));
        var data = calculator.CalculateHistogramValues([dimension], s_startTime, ToLocal, "ms");

        Assert.False(data.HasIncompatibleHistogramBounds);
        Assert.Collection(data.Traces,
            trace => AssertTrace(trace, percentile: 50, value: 10, difference: 10),
            trace => AssertTrace(trace, percentile: 90, value: sameBucket ? 10 : 50, difference: sameBucket ? 0 : 40),
            trace => AssertTrace(trace, percentile: 99, value: sameBucket ? 10 : 100, difference: sameBucket ? 0 : 50));

        void AssertTrace(ChartTrace trace, int percentile, double value, double difference)
        {
            var hasLastValue = temporality == OtlpAggregationTemporality.Cumulative;
            Assert.Equal(percentile, trace.Percentile);
            Assert.Equal([null, null, null, null, null, value, hasLastValue ? value : null], trace.Values);
            Assert.Equal([null, null, null, null, null, difference, hasLastValue ? difference : null], trace.DiffValues);
        }
    }

    [Fact]
    public void TryCalculateHistogramPoints_StaggeredDimensionChanges_CombinesObservationDeltas()
    {
        var context = CreateContext();
        var stableDimension = new DimensionScope(capacity: 100, []);
        var changingDimension = new DimensionScope(capacity: 100, []);
        var start = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        for (var minute = 1; minute <= 3; minute++)
        {
            stableDimension.AddHistogramValue(CreateHistogramPoint(start, minute, 10, [10, 0, 0]), OtlpAggregationTemporality.Cumulative, context);
            changingDimension.AddHistogramValue(CreateHistogramPoint(start, minute, checked((ulong)(15 + (minute * 5))), [0, 0, checked((ulong)(15 + (minute * 5)))]), OtlpAggregationTemporality.Cumulative, context);
        }

        var traces = new Dictionary<int, ChartTrace>
        {
            [25] = new ChartTrace { Name = "P25", Percentile = 25 }
        };
        var exemplars = new List<ChartExemplar>();

        var firstResult = ChartDataCalculator.TryCalculateHistogramPoints(
            [stableDimension, changingDimension],
            start,
            start,
            traces,
            exemplars,
            ToLocal,
            out _);
        var secondResult = ChartDataCalculator.TryCalculateHistogramPoints(
            [stableDimension, changingDimension],
            start.AddMinutes(1),
            start.AddMinutes(1),
            traces,
            exemplars,
            ToLocal,
            out _);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.Equal([10, 100], traces[25].Values);

        static HistogramDataPoint CreateHistogramPoint(DateTimeOffset start, int minute, ulong count, ulong[] bucketCounts)
        {
            var point = new HistogramDataPoint
            {
                StartTimeUnixNano = ToNanos(start),
                TimeUnixNano = ToNanos(start.AddMinutes(minute)),
                Count = count,
                Sum = count
            };
            point.ExplicitBounds.AddRange([10, 100]);
            point.BucketCounts.AddRange(bucketCounts);
            return point;
        }
    }

    [Fact]
    public void CalculateHistogramValues_XValuesInChronologicalOrder()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);

        var histogramPoint = new HistogramDataPoint
        {
            StartTimeUnixNano = 0,
            TimeUnixNano = (ulong)s_startTime.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000,
            Count = 10,
            Sum = 100.0,
        };
        histogramPoint.ExplicitBounds.AddRange([50.0]);
        histogramPoint.BucketCounts.AddRange([5UL, 5UL]);

        dimension.AddHistogramValue(histogramPoint, OtlpAggregationTemporality.Cumulative, context);

        var calculator = new ChartDataCalculator(pointCount: 10, duration: TimeSpan.FromSeconds(100));
        var data = calculator.CalculateHistogramValues([dimension], s_startTime, ToLocal, "ms");

        for (var i = 1; i < data.XValues.Count; i++)
        {
            Assert.True(data.XValues[i] > data.XValues[i - 1],
                $"xValues[{i}] ({data.XValues[i]}) should be after xValues[{i - 1}] ({data.XValues[i - 1]})");
        }
    }

    [Fact]
    public void HistogramBuckets_DifferentLayouts_MergesOnlyAtSharedBoundaries()
    {
        ulong[]? counts = [1, 5, 0];
        double[]? bounds = [10, 100];
        Assert.True(HistogramBuckets.Add(ref counts, ref bounds, [1, 2, 2, 0], [10, 50, 100], []));

        Assert.NotNull(bounds);
        Assert.NotNull(counts);
        Assert.Equal([10d, 100d], bounds);
        Assert.Equal([2ul, 9ul, 0ul], counts);
    }

    [Fact]
    public void HistogramBuckets_DifferentLayouts_PreservesOverflowCounts()
    {
        ulong[]? counts = [1, 2, 3];
        double[]? bounds = [10, 100];
        Assert.True(HistogramBuckets.Add(ref counts, ref bounds, [4, 5, 6, 7], [10, 50, 100], []));

        Assert.NotNull(bounds);
        Assert.NotNull(counts);
        Assert.Equal([10d, 100d], bounds);
        Assert.Equal([5ul, 13ul, 10ul], counts);
    }

    [Fact]
    public void HistogramBuckets_FirstContribution_SubtractsPreviousCountsWithoutChangingSource()
    {
        ulong[] incoming = [3, 5, 7];
        ulong[] previous = [1, 2, 3];
        ulong[]? counts = null;
        double[]? bounds = null;
        Assert.True(HistogramBuckets.Add(ref counts, ref bounds, incoming, [10, 100], previous));

        Assert.NotNull(counts);
        Assert.Equal([2ul, 3ul, 4ul], counts);
        Assert.Equal([3ul, 5ul, 7ul], incoming);
        Assert.Equal([1ul, 2ul, 3ul], previous);
    }

    [Fact]
    public void HistogramBuckets_CommonBoundsAreSubsetOfIncoming_ReusesIncomingBounds()
    {
        ulong[]? counts = [1, 2, 3, 4];
        double[]? bounds = [10, 50, 100];
        double[] incomingBounds = [10, 100];
        Assert.True(HistogramBuckets.Add(ref counts, ref bounds, [5, 6, 7], incomingBounds, []));

        Assert.Same(incomingBounds, bounds);
        Assert.NotNull(counts);
        Assert.Equal([6ul, 11ul, 11ul], counts);
    }

    [Fact]
    public void HistogramBuckets_PartiallySharedBounds_CombinesCumulativeDifferences()
    {
        ulong[]? counts = [1, 2, 3, 4];
        double[]? bounds = [10, 50, 100];
        Assert.True(HistogramBuckets.Add(ref counts, ref bounds, [5, 6, 7, 8], [10, 75, 100], [1, 2, 3, 4]));

        Assert.NotNull(bounds);
        Assert.NotNull(counts);
        Assert.Equal([10d, 100d], bounds);
        Assert.Equal([5ul, 13ul, 8ul], counts);
    }

    [Fact]
    public void HistogramBuckets_NoSharedBounds_CombinesIntoOverflowBucket()
    {
        ulong[]? counts = [1, 2];
        double[]? bounds = [10];
        Assert.True(HistogramBuckets.Add(ref counts, ref bounds, [3, 4], [20], []));

        Assert.NotNull(bounds);
        Assert.NotNull(counts);
        Assert.Empty(bounds);
        Assert.Equal([10ul], counts);
    }

    [Fact]
    public void HistogramBuckets_UnchangedDifferentLayout_DoesNotReduceAccumulatorBounds()
    {
        ulong[] originalCounts = [1, 2, 3];
        double[] originalBounds = [10, 100];
        ulong[]? counts = originalCounts;
        double[]? bounds = originalBounds;
        Assert.False(HistogramBuckets.Add(ref counts, ref bounds, [4, 5, 6], [20, 200], [4, 5, 6]));

        Assert.Same(originalCounts, counts);
        Assert.Same(originalBounds, bounds);
        Assert.Equal([1ul, 2ul, 3ul], originalCounts);
    }

    [Theory]
    [InlineData(OtlpAggregationTemporality.Cumulative)]
    [InlineData(OtlpAggregationTemporality.Delta)]
    public void CalculateHistogramValues_EqualBucketLengthsWithDifferentBounds_UsesSharedBounds(OtlpAggregationTemporality temporality)
    {
        var first = new DimensionScope(100, []);
        var second = new DimensionScope(100, []);
        var time = s_startTime.UtcDateTime.AddSeconds(-1);
        first.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [10, 0, 0], [10, 100]),
            temporality, CreateContext());
        second.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [0, 10, 0], [50, 100]),
            temporality, CreateContext());

        var calculator = new ChartDataCalculator(5, TimeSpan.FromSeconds(10));
        var data = calculator.CalculateHistogramValues([first, second], s_startTime, ToLocal, "ms");
        Assert.False(data.HasIncompatibleHistogramBounds);
        Assert.All(data.Traces, trace => Assert.All(trace.Values.OfType<double>(), value => Assert.Equal(100, value)));
        Assert.All(data.Traces, trace => Assert.Contains(100d, trace.Values));
        Assert.False(calculator.CalculateChartValues([first, second], s_startTime, ToLocal, "Count").HasIncompatibleHistogramBounds);
    }

    [Theory]
    [InlineData(15ul, false)]
    [InlineData(20ul, false)]
    [InlineData(25ul, false)]
    [InlineData(15ul, true)]
    public void AddHistogramValue_StaleCumulativePoint_RejectsBeforeMutatingAggregation(ulong staleCount, bool changedStart)
    {
        var context = CreateContext();
        var dimension = new DimensionScope(100, []);
        var time = s_startTime.UtcDateTime;
        dimension.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [10, 0], [100]),
            OtlpAggregationTemporality.Cumulative, context);
        dimension.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(200), [20, 0], [100]),
            OtlpAggregationTemporality.Cumulative, context);
        var previous = Assert.IsType<HistogramValue>(dimension.Values[1]);
        var stale = HistogramTestHelpers.CreatePoint(
            changedStart ? time.AddSeconds(-1) : time, time.AddMilliseconds(150), [staleCount, 0], [100]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => dimension.AddHistogramValue(stale, OtlpAggregationTemporality.Cumulative, context));

        Assert.Equal("Cumulative histogram point timestamp is earlier than the previous point.", exception.Message);
        Assert.Equal(2, dimension.Values.Count);
        Assert.Equal(time.AddMilliseconds(200), previous.End);
        Assert.Equal(20ul, previous.Count);
        dimension.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(300), [25, 0], [100]),
            OtlpAggregationTemporality.Cumulative, context);
        Assert.Equal(
            [(time, time.AddMilliseconds(100), 10ul),
             (time.AddMilliseconds(100), time.AddMilliseconds(200), 20ul),
             (time.AddMilliseconds(200), time.AddMilliseconds(300), 25ul)],
            dimension.Values.Cast<HistogramValue>().Select(value => (value.Start, value.End, value.Count)));
        Assert.All(dimension.Values.Cast<HistogramValue>(), value => Assert.Equal(previous.AggregationId, value.AggregationId));

        var traces = new Dictionary<int, ChartTrace>
        {
            [50] = new() { Name = "P50", Percentile = 50 },
            [90] = new() { Name = "P90", Percentile = 90 },
            [99] = new() { Name = "P99", Percentile = 99 }
        };
        Assert.True(ChartDataCalculator.TryCalculateHistogramPoints([dimension], s_startTime, s_startTime.AddSeconds(1),
            traces, [], ToLocal, out var incompatibleBounds));
        Assert.False(incompatibleBounds);
        Assert.All(traces.Values, trace => Assert.Equal([100d], trace.Values));
        Assert.True(ChartDataCalculator.TryCalculatePoint([dimension], s_startTime, s_startTime.AddSeconds(1), out var count));
        Assert.Equal(25, count);
    }

    [Theory]
    [InlineData(OtlpAggregationTemporality.Cumulative)]
    [InlineData(OtlpAggregationTemporality.Delta)]
    public void CalculateHistogramValues_NoSharedBounds_ReportsUnavailablePercentilesAndRetainsCount(OtlpAggregationTemporality temporality)
    {
        var first = new DimensionScope(100, []);
        var second = new DimensionScope(100, []);
        var time = s_startTime.UtcDateTime.AddSeconds(-1);
        first.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [3, 0], [10]),
            temporality, CreateContext());
        second.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [5, 0], [20]),
            temporality, CreateContext());

        var calculator = new ChartDataCalculator(5, TimeSpan.FromSeconds(10));
        var data = calculator.CalculateHistogramValues([first, second], s_startTime, ToLocal, "ms");
        Assert.True(data.HasIncompatibleHistogramBounds);
        Assert.All(data.Traces, trace =>
        {
            Assert.Equal([null, null, null, null, null, null, null], trace.Values);
            Assert.Equal(trace.Values, trace.DiffValues);
        });
        Assert.True(ChartDataCalculator.TryCalculatePoint([first, second], time, time.AddSeconds(1), out var count));
        Assert.Equal(8, count);
        var countData = calculator.CalculateChartValues([first, second], s_startTime, ToLocal, "Count");
        Assert.True(countData.HasIncompatibleHistogramBounds);
        Assert.Contains(8d, Assert.Single(countData.Traces).Values);

        var filteredData = calculator.CalculateChartValues([first], s_startTime, ToLocal, "Count");
        Assert.False(filteredData.HasIncompatibleHistogramBounds);
        Assert.Contains(3d, Assert.Single(filteredData.Traces).Values);
        Assert.False(calculator.CalculateChartValues([first, second], s_startTime.AddMinutes(1), ToLocal, "Count").HasIncompatibleHistogramBounds);
    }

    [Theory]
    [InlineData(OtlpAggregationTemporality.Cumulative)]
    [InlineData(OtlpAggregationTemporality.Delta)]
    public void CalculateChartValues_UnchangedIncompatibleHistogram_DoesNotWarn(OtlpAggregationTemporality temporality)
    {
        var first = new DimensionScope(100, []);
        var second = new DimensionScope(100, []);
        var time = s_startTime.UtcDateTime.AddSeconds(-1);
        first.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [3, 0], [10]),
            temporality, CreateContext());
        second.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [0, 0], [20]),
            temporality, CreateContext());

        var calculator = new ChartDataCalculator(5, TimeSpan.FromSeconds(10));
        Assert.False(calculator.CalculateChartValues([first, second], s_startTime, ToLocal, "Count").HasIncompatibleHistogramBounds);
    }

    [Fact]
    public void CalculateChartValues_PairwiseSharedButNoCommonHistogramBounds_Warns()
    {
        var dimensions = new List<DimensionScope>();
        double[][] layouts = [[10, 100], [10, 200], [100, 200]];
        var time = s_startTime.UtcDateTime.AddSeconds(-1);
        foreach (var layout in layouts)
        {
            var dimension = new DimensionScope(100, []);
            dimension.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddMilliseconds(100), [1, 0, 0], layout),
                OtlpAggregationTemporality.Delta, CreateContext());
            dimensions.Add(dimension);
        }

        var calculator = new ChartDataCalculator(5, TimeSpan.FromSeconds(10));
        var data = calculator.CalculateChartValues(dimensions, s_startTime, ToLocal, "Count");
        Assert.True(data.HasIncompatibleHistogramBounds);
        Assert.Contains(3d, Assert.Single(data.Traces).Values);
    }

    [Fact]
    public void TryCalculateHistogramPoints_DeltaBoundary_BelongsToOneWindow()
    {
        var dimension = new DimensionScope(100, []);
        var time = s_startTime.UtcDateTime;
        dimension.AddHistogramValue(HistogramTestHelpers.CreatePoint(time, time.AddSeconds(1), [10, 0, 0], [10, 100]),
            OtlpAggregationTemporality.Delta, CreateContext());
        dimension.AddHistogramValue(HistogramTestHelpers.CreatePoint(time.AddSeconds(1), time.AddSeconds(2), [0, 10, 0], [10, 100]),
            OtlpAggregationTemporality.Delta, CreateContext());
        var traces = new Dictionary<int, ChartTrace> { [50] = new() { Name = "P50", Percentile = 50 } };

        Assert.True(ChartDataCalculator.TryCalculateHistogramPoints([dimension], s_startTime, s_startTime.AddSeconds(1),
            traces, [], ToLocal, out _));
        Assert.True(ChartDataCalculator.TryCalculateHistogramPoints([dimension], s_startTime.AddSeconds(1), s_startTime.AddSeconds(2),
            traces, [], ToLocal, out _));
        Assert.Equal([10d, 100d], traces[50].Values);
        Assert.True(ChartDataCalculator.TryCalculatePoint([dimension], s_startTime, s_startTime.AddSeconds(1), out var count));
        Assert.Equal(10, count);
    }

    [Fact]
    public void CalculateChartValues_ToLocalApplied()
    {
        var context = CreateContext();
        var dimension = new DimensionScope(capacity: 100, []);
        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 1,
            StartTimeUnixNano = 0,
            TimeUnixNano = long.MaxValue
        }, context);

        // Apply a +5 hour offset to simulate a time zone.
        var offset = TimeSpan.FromHours(5);
        DateTimeOffset toLocalWithOffset(DateTimeOffset dt) => dt.ToOffset(offset);

        var calculator = new ChartDataCalculator(pointCount: 5, duration: TimeSpan.FromSeconds(50));
        var data = calculator.CalculateChartValues([dimension], s_startTime, toLocalWithOffset, "Count");

        Assert.All(data.XValues, x => Assert.Equal(offset, x.Offset));
    }

    [Fact]
    public void MetricInstrumentDataCache_Merge_ReplacesTailAndAddsDimension()
    {
        var start = s_startTime.UtcDateTime;
        KeyValuePair<string, string>[] firstAttributes = [KeyValuePair.Create("dimension", "first")];
        KeyValuePair<string, string>[] secondAttributes = [KeyValuePair.Create("dimension", "second")];
        var cachedDimension = new DimensionScope(capacity: 100, firstAttributes);
        cachedDimension.Values.Add(new MetricValue<long>(1, start, start.AddSeconds(1)));
        cachedDimension.Values.Add(new MetricValue<long>(2, start.AddSeconds(1), start.AddSeconds(2)));

        var refreshedDimension = new DimensionScope(capacity: 100, firstAttributes);
        refreshedDimension.Values.Add(new MetricValue<long>(1, start, start.AddSeconds(1)));
        refreshedDimension.Values.Add(new MetricValue<long>(2, start.AddSeconds(1), start.AddSeconds(3)));
        refreshedDimension.Values.Add(new MetricValue<long>(3, start.AddSeconds(3), start.AddSeconds(4)));
        var newDimension = new DimensionScope(capacity: 100, secondAttributes);
        newDimension.Values.Add(new MetricValue<long>(4, start.AddSeconds(3), start.AddSeconds(4)));

        var cached = CreateInstrumentData([cachedDimension]);
        var refreshed = CreateInstrumentData([refreshedDimension, newDimension]);
        var cursors = new List<MetricDimensionCursor>
        {
            new() { Attributes = firstAttributes, StartTime = start.AddSeconds(1) }
        };

        var merged = MetricInstrumentDataCache.Merge(cached, refreshed, cursors, start);

        Assert.Collection(
            merged.Dimensions[0].Values.Cast<MetricValue<long>>(),
            value => Assert.Equal((1L, start.AddSeconds(1)), (value.Value, value.End)),
            value => Assert.Equal((2L, start.AddSeconds(3)), (value.Value, value.End)),
            value => Assert.Equal((3L, start.AddSeconds(4)), (value.Value, value.End)));
        Assert.Equal(4, Assert.IsType<MetricValue<long>>(Assert.Single(merged.Dimensions[1].Values)).Value);
    }

    [Fact]
    public void MetricInstrumentDataCache_Merge_ReplacesLateArrivingValue()
    {
        var start = s_startTime.UtcDateTime;
        KeyValuePair<string, string>[] attributes = [KeyValuePair.Create("dimension", "first")];
        var cachedDimension = new DimensionScope(capacity: 100, attributes);
        cachedDimension.Values.Add(new MetricValue<long>(1, start, start.AddSeconds(5)));
        cachedDimension.Values.Add(new MetricValue<long>(2, start.AddSeconds(14), start.AddSeconds(15)));
        cachedDimension.Values.Add(new MetricValue<long>(3, start.AddSeconds(29), start.AddSeconds(30)));

        var refreshedDimension = new DimensionScope(capacity: 100, attributes);
        refreshedDimension.Values.Add(new MetricValue<long>(20, start.AddSeconds(14), start.AddSeconds(15)));
        refreshedDimension.Values.Add(new MetricValue<long>(3, start.AddSeconds(29), start.AddSeconds(30)));

        var cached = CreateInstrumentData([cachedDimension]);
        var refreshed = CreateInstrumentData([refreshedDimension]);
        var cursors = MetricInstrumentDataCache.CreateCursors(cached, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));

        Assert.Equal(start.AddSeconds(10), Assert.Single(cursors).StartTime);

        var merged = MetricInstrumentDataCache.Merge(cached, refreshed, cursors, start);

        Assert.Collection(
            Assert.Single(merged.Dimensions).Values.Cast<MetricValue<long>>(),
            value => Assert.Equal((1L, start.AddSeconds(5)), (value.Value, value.End)),
            value => Assert.Equal((20L, start.AddSeconds(15)), (value.Value, value.End)),
            value => Assert.Equal((3L, start.AddSeconds(30)), (value.Value, value.End)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(15, 2)]
    [InlineData(16, 5)]
    [InlineData(30, 5)]
    [InlineData(31, 10)]
    [InlineData(60, 10)]
    [InlineData(61, 30)]
    [InlineData(180, 30)]
    [InlineData(181, 60)]
    [InlineData(360, 60)]
    [InlineData(361, 120)]
    [InlineData(720, 120)]
    [InlineData(721, 300)]
    public void MetricDataPointInterval_Get_ReturnsDurationResolution(int durationMinutes, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MetricDataPointInterval.Get(TimeSpan.FromMinutes(durationMinutes)));
    }

    private static OtlpInstrumentData CreateInstrumentData(List<DimensionScope> dimensions)
    {
        var resource = new OtlpResource("resource", instanceId: null, uninstrumentedPeer: false, CreateContext());

        return new OtlpInstrumentData
        {
            Summary = new OtlpInstrumentSummary
            {
                Name = "test",
                Description = "test",
                Unit = "items",
                Type = OtlpInstrumentType.Gauge,
                AggregationTemporality = OtlpAggregationTemporality.Cumulative,
                Parent = OtlpScope.Empty,
                ResourceView = new OtlpResourceView(resource, Array.Empty<KeyValuePair<string, string>>())
            },
            Dimensions = dimensions,
            KnownAttributeValues = [],
            HasOverflow = false
        };
    }

    private static ulong ToNanos(DateTimeOffset dt) => (ulong)(dt.ToUnixTimeMilliseconds() * 1_000_000);
}
