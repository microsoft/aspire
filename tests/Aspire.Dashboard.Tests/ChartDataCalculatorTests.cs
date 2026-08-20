// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
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

        dimension.AddHistogramValue(histogramPoint, context);

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

        dimension.AddHistogramValue(histogramPoint, context);

        var calculator = new ChartDataCalculator(pointCount: 10, duration: TimeSpan.FromSeconds(100));
        var data = calculator.CalculateHistogramValues([dimension], s_startTime, ToLocal, "ms");

        for (var i = 1; i < data.XValues.Count; i++)
        {
            Assert.True(data.XValues[i] > data.XValues[i - 1],
                $"xValues[{i}] ({data.XValues[i]}) should be after xValues[{i - 1}] ({data.XValues[i - 1]})");
        }
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

    private static ulong ToNanos(DateTimeOffset dt) => (ulong)(dt.ToUnixTimeMilliseconds() * 1_000_000);
}
