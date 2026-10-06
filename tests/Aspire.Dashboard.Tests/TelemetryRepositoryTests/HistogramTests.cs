// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.HistogramTestHelpers;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public sealed class HistogramTests(ITestOutputHelper testOutputHelper) : TelemetryRepositoryTestBase
{
    private static readonly DateTime s_start = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Histogram_OverflowingCount_RejectsOnlyInvalidPoint(bool overflowTotalCount)
    {
        using var context = await CreateRepositoryAsync();
        var invalid = CreatePoint(s_start, s_start.AddMilliseconds(100), [1, 0], [100]);
        if (overflowTotalCount)
        {
            invalid.Count = ulong.MaxValue;
        }
        else
        {
            invalid.BucketCounts[0] = ulong.MaxValue;
        }
        var valid = CreatePoint(s_start, s_start.AddMilliseconds(200), [1, 0], [100]);
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative, invalid, valid)]);

        Assert.Equal(1, addContext.FailureCount);
        Assert.Equal(1, addContext.SuccessCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: false);
        var value = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values));
        Assert.Equal([1ul, 0ul], value.Values);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delta_EqualCounts_PreservesEveryInterval(bool separateRequests, bool rollup)
    {
        using var context = await CreateRepositoryAsync();
        var points = new[]
        {
            CreatePoint(s_start, s_start.AddMilliseconds(100), [100, 0, 0], [10, 100]),
            CreatePoint(s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), [0, 100, 0], [10, 100])
        };
        var addContext = new AddContext();
        if (separateRequests)
        {
            foreach (var point in points)
            {
                await context.Repository.AsWriter().AddMetricsAsync(addContext,
                    [CreateMetrics(AggregationTemporality.Delta, point)]);
            }
        }
        else
        {
            await context.Repository.AsWriter().AddMetricsAsync(addContext,
                [CreateMetrics(AggregationTemporality.Delta, points)]);
        }

        var instrument = await GetInstrumentAsync(context.Repository, rollup);
        Assert.Equal(2, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        Assert.Collection(Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>(),
            value =>
            {
                Assert.Equal(s_start, value.Start);
                Assert.Equal([100ul, 0ul, 0ul], value.Values);
            },
            value =>
            {
                Assert.Equal(s_start.AddMilliseconds(100), value.Start);
                Assert.Equal([0ul, 100ul, 0ul], value.Values);
            });

        AssertPercentiles(instrument, [10, 100, 100]);
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(200, count);
    }

    [Fact]
    public async Task Delta_DecreasingCountsAndChangedLayout_CombinesIntervals()
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Delta,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [1, 8, 1], [10, 100]),
                CreatePoint(s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), [0, 0, 2, 0], [10, 50, 100]))]);

        Assert.Equal(0, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        AssertPercentiles(instrument, [100, 100, 100]);
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(12, count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cumulative_ResetWithinRollup_RetainsBothAggregations(bool explicitStart, bool separateRequests)
    {
        using var context = await CreateRepositoryAsync();
        var resetStart = explicitStart ? s_start.AddMilliseconds(250) : s_start;
        var points = new[]
        {
            CreatePoint(s_start, s_start.AddMilliseconds(100), [100, 0, 0], [10, 100]),
            CreatePoint(s_start, s_start.AddMilliseconds(200), [110, 10, 0], [10, 100]),
            CreatePoint(resetStart, s_start.AddMilliseconds(300), [0, 1, 0], [10, 100]),
            CreatePoint(resetStart, s_start.AddMilliseconds(400), [0, 10, 0], [10, 100])
        };
        var addContext = new AddContext();
        foreach (var batch in separateRequests ? points.Select(point => new[] { point }) : [points])
        {
            await context.Repository.AsWriter().AddMetricsAsync(addContext,
                [CreateMetrics(AggregationTemporality.Cumulative, batch)]);
        }

        Assert.Equal(4, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal([120ul, 10ul], values.Select(value => value.Count));
        Assert.NotEqual(values[0].AggregationId, values[1].AggregationId);
        Assert.Equal(resetStart, values[1].AggregationStart);
        AssertPercentiles(instrument, [10, 100, 100]);
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(10, count);
    }

    [Fact]
    public async Task Cumulative_ChangedStartWithSameCount_DoesNotMerge()
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0], [100]),
                CreatePoint(s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), [10, 0], [100]))]);

        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal(2, values.Length);
        Assert.NotEqual(values[0].AggregationId, values[1].AggregationId);
    }

    [Fact]
    public async Task Cumulative_DecreasingBucketWithIncreasingTotal_StartsNewAggregation()
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0, 0], [10, 100]),
                CreatePoint(s_start, s_start.AddMilliseconds(200), [0, 20, 0], [10, 100]))]);

        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        Assert.Equal(2, Assert.Single(instrument.Dimensions).Values.Count);
        AssertPercentiles(instrument, [100, 100, 100]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cumulative_LayoutChangesWithoutReset_RejectsPoint(bool changedLength)
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [1, 1, 0], [10, 100]),
                CreatePoint(s_start, s_start.AddMilliseconds(200),
                    changedLength ? [1, 1, 1, 0] : [1, 2, 0],
                    changedLength ? [10, 50, 100] : [10, 50]))]);

        Assert.Equal(1, addContext.SuccessCount);
        Assert.Equal(1, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: false);
        Assert.Equal([10d, 100d], Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values)).ExplicitBounds);
    }

    [Fact]
    public async Task Cumulative_LayoutChangesAfterReset_AcceptsNewAggregation()
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [1, 1, 0], [10, 100]),
                CreatePoint(s_start.AddMilliseconds(150), s_start.AddMilliseconds(200), [0, 0, 3, 0], [10, 50, 100]))]);

        Assert.Equal(0, addContext.FailureCount);
        AssertPercentiles(await GetInstrumentAsync(context.Repository, rollup: true), [100, 100, 100]);
    }

    [Fact]
    public async Task Cumulative_ReopenedDatabase_RetainsResetAndLayoutChecks()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            await context.Repository.AddMetricsAsync(new AddContext(),
                [CreateMetrics(AggregationTemporality.Cumulative,
                    CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0, 0], [10, 100]),
                    CreatePoint(s_start, s_start.AddMilliseconds(200), [0, 1, 0], [10, 100]))]);
        }

        using var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath);
        var addContext = new AddContext();
        await reopened.Repository.AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(300), [0, 5, 0], [10, 100]),
                CreatePoint(s_start, s_start.AddMilliseconds(400), [0, 6, 0], [10, 50]))]);
        Assert.Equal(1, addContext.SuccessCount);
        Assert.Equal(1, addContext.FailureCount);
        AssertPercentiles(await GetInstrumentAsync(reopened.Repository, rollup: true), [10, 100, 100]);
    }

    [Fact]
    public async Task Histogram_TemporalityChange_RejectsPoint()
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative, CreatePoint(s_start, s_start.AddSeconds(1), [1, 0], [100]))]);
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Delta, CreatePoint(s_start.AddSeconds(1), s_start.AddSeconds(2), [1, 0], [100]))]);
        Assert.Equal(1, addContext.SuccessCount);
        Assert.Equal(1, addContext.FailureCount);
    }

    [Fact]
    public async Task Delta_ExemplarsWithinRollup_RemainAttachedToTheirIntervals()
    {
        using var context = await CreateRepositoryAsync();
        var first = CreatePoint(s_start, s_start.AddMilliseconds(100), [1, 0], [100]);
        var second = CreatePoint(s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), [1, 0], [100]);
        first.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(50), 1));
        second.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(150), 2));
        await context.Repository.AsWriter().AddMetricsAsync(new AddContext(),
            [CreateMetrics(AggregationTemporality.Delta, first, second)]);

        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        Assert.Collection(Assert.Single(instrument.Dimensions).Values,
            value => Assert.Equal(1, Assert.Single(value.Exemplars).Value),
            value => Assert.Equal(2, Assert.Single(value.Exemplars).Value));
    }

    [Fact]
    public async Task Cumulative_ResetWithinRollup_ExemplarsDoNotCrossAggregations()
    {
        using var context = await CreateRepositoryAsync();
        var first = CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0], [100]);
        var beforeReset = CreatePoint(s_start, s_start.AddMilliseconds(200), [20, 0], [100]);
        var reset = CreatePoint(s_start.AddMilliseconds(250), s_start.AddMilliseconds(300), [1, 0], [100]);
        var afterReset = CreatePoint(s_start.AddMilliseconds(250), s_start.AddMilliseconds(400), [2, 0], [100]);
        first.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(50), 1));
        reset.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(275), 2));
        await context.Repository.AsWriter().AddMetricsAsync(new AddContext(),
            [CreateMetrics(AggregationTemporality.Cumulative, first, beforeReset, reset, afterReset)]);

        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        Assert.Collection(Assert.Single(instrument.Dimensions).Values,
            value => Assert.Equal(1, Assert.Single(value.Exemplars).Value),
            value => Assert.Equal(2, Assert.Single(value.Exemplars).Value));
    }

    [Fact]
    public async Task Cumulative_Export_PreservesAggregationStartRatherThanChartInterval()
    {
        using var context = await CreateRepositoryAsync();
        await context.Repository.AsWriter().AddMetricsAsync(new AddContext(),
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [1, 0], [100]),
                CreatePoint(s_start, s_start.AddMilliseconds(200), [2, 0], [100]))]);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: false);
        var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal(s_start.AddMilliseconds(100), values[1].Start);
        Assert.Equal(s_start, values[1].AggregationStart);

        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
        var resource = Assert.Single(exported.ResourceMetrics!);
        var scope = Assert.Single(resource.ScopeMetrics!);
        var histogram = Assert.Single(scope.Metrics!).Histogram;
        Assert.NotNull(histogram);
        Assert.Equal((int)AggregationTemporality.Cumulative, histogram.AggregationTemporality);
        Assert.Collection(histogram.DataPoints!,
            point => Assert.Equal(DateTimeToUnixNanoseconds(s_start), point.StartTimeUnixNano),
            point => Assert.Equal(DateTimeToUnixNanoseconds(s_start), point.StartTimeUnixNano));
    }

    private static async Task<OtlpInstrumentData> GetInstrumentAsync(ITelemetryRepository repository, bool rollup)
    {
        var instrument = await repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = s_start,
            EndTime = s_start.AddMinutes(1),
            DataPointInterval = rollup ? TimeSpan.FromMinutes(1) : null
        }, CancellationToken.None);
        Assert.NotNull(instrument);
        return instrument;
    }

    private static void AssertPercentiles(OtlpInstrumentData instrument, double?[] expected)
    {
        var traces = new Dictionary<int, ChartTrace>
        {
            [50] = new() { Name = "P50", Percentile = 50 },
            [90] = new() { Name = "P90", Percentile = 90 },
            [99] = new() { Name = "P99", Percentile = 99 }
        };
        Assert.True(ChartDataCalculator.TryCalculateHistogramPoints(instrument.Dimensions,
            s_start, s_start.AddSeconds(1), traces, [], value => value, out var incompatibleBounds));
        Assert.False(incompatibleBounds);
        Assert.Equal(expected, traces.Values.Select(trace => Assert.Single(trace.Values)));
    }
}
