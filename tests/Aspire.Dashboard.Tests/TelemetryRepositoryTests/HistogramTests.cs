// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Components;
using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Model.Serialization;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.HistogramTestHelpers;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public sealed class HistogramTests(ITestOutputHelper testOutputHelper) : TelemetryRepositoryTestBase
{
    private static readonly DateTime s_start = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(AggregationTemporality.Cumulative, false)]
    [InlineData(AggregationTemporality.Cumulative, true)]
    [InlineData(AggregationTemporality.Delta, false)]
    [InlineData(AggregationTemporality.Delta, true)]
    public async Task Exemplars_SubTickTimestamps_PreserveIdentityThroughReopeningAndExport(AggregationTemporality temporality, bool rollup)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var sampleTime = s_start.AddMilliseconds(500);
        var first = CreateExemplar(sampleTime, 1, [KeyValuePair.Create("sample", "first")]);
        first.TimeUnixNano += 10;
        var second = CreateExemplar(sampleTime, 1, [KeyValuePair.Create("sample", "second")]);
        second.TimeUnixNano += 20;
        var point = CreatePoint(s_start, s_start.AddSeconds(1), [3, 0], [100]);
        point.Exemplars.AddRange([first, second, first.Clone()]);
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(temporality, point, point.Clone())]);
            Assert.Equal(2, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
        }
        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            await reopened.Repository.AddMetricsAsync(new AddContext(), [CreateMetrics(temporality, point)]);
        }

        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        var instrument = await GetInstrumentAsync(readOnly.Repository, rollup);
        var value = Assert.Single(Assert.Single(instrument.Dimensions).Values);
        var expectedTimestamps = new[] { first.TimeUnixNano, second.TimeUnixNano };
        Assert.Equal(expectedTimestamps, value.Exemplars.Select(exemplar => exemplar.TimeUnixNano));
        Assert.All(value.Exemplars, exemplar =>
        {
            Assert.Equal(sampleTime, exemplar.Start);
            Assert.Equal(first.SpanId.ToHexString(), exemplar.SpanId);
            Assert.Equal(first.TraceId.ToHexString(), exemplar.TraceId);
        });
        Assert.Equal(["first", "second"], value.Exemplars.Select(exemplar => Assert.Single(exemplar.Attributes).Value));
        var cloned = MetricValueBase.Clone(value);
        Assert.Equal(expectedTimestamps, cloned.Exemplars.Select(exemplar => exemplar.TimeUnixNano));
        var cursors = MetricInstrumentDataCache.CreateCursors(instrument, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        var merged = MetricInstrumentDataCache.Merge(instrument, instrument, cursors, s_start);
        Assert.Equal(expectedTimestamps, Assert.Single(Assert.Single(merged.Dimensions).Values).Exemplars.Select(exemplar => exemplar.TimeUnixNano));

        var filtered = await readOnly.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = s_start,
            EndTime = sampleTime,
            DataPointInterval = rollup ? TimeSpan.FromMinutes(1) : null
        }, CancellationToken.None);
        Assert.NotNull(filtered);
        Assert.Equal(expectedTimestamps, Assert.Single(Assert.Single(filtered.Dimensions).Values).Exemplars.Select(exemplar => exemplar.TimeUnixNano));
        var afterSamples = await readOnly.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = sampleTime.AddTicks(1),
            EndTime = s_start.AddMinutes(1),
            DataPointInterval = rollup ? TimeSpan.FromMinutes(1) : null
        }, CancellationToken.None);
        Assert.NotNull(afterSamples);
        Assert.Empty(Assert.Single(Assert.Single(afterSamples.Dimensions).Values).Exemplars);

        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([merged]);
        var exportedPoint = Assert.Single(Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!).Histogram!.DataPoints!);
        Assert.Equal(expectedTimestamps, exportedPoint.Exemplars!.Select(exemplar => exemplar.TimeUnixNano!.Value));
        var json = JsonSerializer.Serialize(exported, OtlpJsonSerializerContext.DefaultContext.OtlpTelemetryDataJson);
        var deserialized = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.DefaultContext.OtlpTelemetryDataJson);
        Assert.NotNull(deserialized);
        var request = OtlpJsonToProtobufConverter.ToProtobuf(new OtlpExportMetricsServiceRequestJson
        {
            ResourceMetrics = deserialized.ResourceMetrics
        });
        using var destination = await CreateRepositoryAsync();
        await destination.Repository.AsWriter().AddMetricsAsync(new AddContext(), request.ResourceMetrics);
        var imported = await GetInstrumentAsync(destination.Repository, rollup);
        Assert.Equal(expectedTimestamps, Assert.Single(Assert.Single(imported.Dimensions).Values).Exemplars.Select(exemplar => exemplar.TimeUnixNano));
    }

    [Theory]
    [InlineData(9223372036854775808ul)]
    [InlineData(ulong.MaxValue)]
    public async Task Exemplars_OutOfRangeTimestamp_LogsAndDropsSampleWithoutRejectingMeasurement(ulong rejectedTimestamp)
    {
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        using var context = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository(loggerFactory: loggerFactory);
        var point = CreatePoint(s_start, s_start.AddSeconds(1), [1, 0], [100]);
        point.TimeUnixNano = long.MaxValue;
        var accepted = CreateExemplar(s_start, 1);
        accepted.TimeUnixNano = long.MaxValue;
        var rejected = accepted.Clone();
        rejected.TimeUnixNano = rejectedTimestamp;
        point.Exemplars.AddRange([accepted, rejected]);
        var addContext = new AddContext();
        await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, point)]);
        Assert.Equal(1, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        var instrument = await context.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        }, CancellationToken.None);
        Assert.NotNull(instrument);
        var value = Assert.Single(Assert.Single(instrument.Dimensions).Values);
        Assert.Equal(accepted.TimeUnixNano, Assert.Single(value.Exemplars).TimeUnixNano);
        var warning = Assert.Single(sink.Writes, write => write.LogLevel == LogLevel.Warning);
        Assert.Equal($"Ignoring metric exemplar with timestamp {rejectedTimestamp} above the signed Unix nanosecond limit.", warning.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cumulative_UnchangedSnapshots_ExtendEndAndPreserveExemplars(bool sameRequest)
    {
        using var context = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository(maxMetricsCount: 1);
        var points = Enumerable.Range(1, 3).Select(index =>
        {
            var point = CreatePoint(s_start, s_start.AddMilliseconds(index * 100), [10, 0], [100]);
            point.StartTimeUnixNano += 10;
            point.TimeUnixNano += 20;
            point.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(index * 100 - 50), index));
            return point;
        }).ToArray();
        var addContext = new AddContext();
        foreach (var batch in sameRequest ? [points] : points.Select(point => new[] { point }))
        {
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, batch)]);
        }

        Assert.Equal(3, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: false);
        var snapshot = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values));
        Assert.Equal(s_start, snapshot.Start);
        Assert.Equal(s_start.AddMilliseconds(300), snapshot.End);
        Assert.Equal(10UL, snapshot.Count);
        Assert.Equal(points[0].StartTimeUnixNano, snapshot.AggregationStartUnixNano);
        Assert.Equal(points[^1].TimeUnixNano, snapshot.EndTimeUnixNano);
        Assert.Equal([1d, 2d, 3d], snapshot.Exemplars.Select(exemplar => exemplar.Value));
        using var connection = context.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_points;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(2ul)]
    public async Task Cumulative_ReopenedDatabase_RejectsSubTickStalePointAndExtendsOriginalEnd(ulong staleCount)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var first = CreatePoint(s_start, s_start, [1, 0], [100]);
        first.StartTimeUnixNano += 10;
        first.TimeUnixNano += 80;
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            await context.Repository.AddMetricsAsync(new AddContext(), [CreateMetrics(AggregationTemporality.Cumulative, first)]);
        }

        var stale = CreatePoint(s_start, s_start, [staleCount, 0], [100]);
        stale.StartTimeUnixNano = first.StartTimeUnixNano;
        stale.TimeUnixNano += 20;
        var continued = first.Clone();
        continued.TimeUnixNano += 10;
        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, stale, continued)]);
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(1, addContext.FailureCount);
        }

        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        var instrument = await GetInstrumentAsync(readOnly.Repository, rollup: true);
        var snapshot = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values));
        Assert.Equal(1ul, snapshot.Count);
        Assert.Equal(first.StartTimeUnixNano, snapshot.AggregationStartUnixNano);
        Assert.Equal(continued.TimeUnixNano, snapshot.EndTimeUnixNano);
        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
        var exportedPoint = Assert.Single(Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!).Histogram!.DataPoints!);
        Assert.Equal(first.StartTimeUnixNano, exportedPoint.StartTimeUnixNano);
        Assert.Equal(continued.TimeUnixNano, exportedPoint.TimeUnixNano);
    }

    [Theory]
    [InlineData(15ul, false)]
    [InlineData(15ul, true)]
    [InlineData(20ul, false)]
    [InlineData(20ul, true)]
    [InlineData(25ul, false)]
    [InlineData(25ul, true)]
    public async Task Cumulative_ReorderedArrivals_RejectsStalePoint(ulong staleCount, bool separateRequests)
    {
        using var context = await CreateRepositoryAsync();
        var points = new[]
        {
            CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0], [100]),
            CreatePoint(s_start, s_start.AddMilliseconds(200), [20, 0], [100]),
            CreatePoint(s_start, s_start.AddMilliseconds(150), [staleCount, 0], [100]),
            CreatePoint(s_start, s_start.AddMilliseconds(300), [25, 0], [100])
        };
        var addContext = new AddContext();
        foreach (var batch in separateRequests ? points.Select(point => new[] { point }) : [points])
        {
            await context.Repository.AsWriter().AddMetricsAsync(addContext,
                [CreateMetrics(AggregationTemporality.Cumulative, batch)]);
        }

        Assert.Equal(3, addContext.SuccessCount);
        Assert.Equal(1, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: false);
        var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal(
            [(s_start, s_start.AddMilliseconds(100), 10ul),
             (s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), 20ul),
             (s_start.AddMilliseconds(200), s_start.AddMilliseconds(300), 25ul)],
            values.Select(value => (value.Start, value.End, value.Count)));
        Assert.All(values, value => Assert.Equal(values[0].AggregationId, value.AggregationId));
        AssertPercentiles(instrument, [100, 100, 100]);
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(25, count);

        var rolledUp = await GetInstrumentAsync(context.Repository, rollup: true);
        var rolledUpValue = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(rolledUp.Dimensions).Values));
        Assert.Equal(25ul, rolledUpValue.Count);
        Assert.Equal(s_start, rolledUpValue.Start);
        Assert.Equal(s_start.AddMilliseconds(300), rolledUpValue.End);
        Assert.Equal(values[0].AggregationId, rolledUpValue.AggregationId);
        AssertPercentiles(rolledUp, [100, 100, 100]);
    }

    [Theory]
    [InlineData(15ul)]
    [InlineData(20ul)]
    [InlineData(25ul)]
    public async Task Cumulative_ReopenedDatabase_RejectsStalePoint(ulong staleCount)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            await context.Repository.AddMetricsAsync(new AddContext(),
                [CreateMetrics(AggregationTemporality.Cumulative,
                    CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0], [100]),
                    CreatePoint(s_start, s_start.AddMilliseconds(200), [20, 0], [100]))]);
        }

        using var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath);
        var addContext = new AddContext();
        await reopened.Repository.AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddMilliseconds(150), [staleCount, 0], [100]),
                CreatePoint(s_start, s_start.AddMilliseconds(300), [25, 0], [100]))]);

        Assert.Equal(1, addContext.SuccessCount);
        Assert.Equal(1, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(reopened.Repository, rollup: false);
        var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal([10ul, 20ul, 25ul], values.Select(value => value.Count));
        Assert.All(values, value => Assert.Equal(values[0].AggregationId, value.AggregationId));
        Assert.Equal(s_start.AddMilliseconds(200), values[^1].Start);
        AssertPercentiles(await GetInstrumentAsync(reopened.Repository, rollup: true), [100, 100, 100]);
    }

    [Fact]
    public async Task Delta_ReorderedArrivals_RetainsAllIntervals()
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Delta,
                CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0], [100]),
                CreatePoint(s_start.AddMilliseconds(200), s_start.AddMilliseconds(300), [25, 0], [100]),
                CreatePoint(s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), [20, 0], [100]))]);

        Assert.Equal(3, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        var instrument = await GetInstrumentAsync(context.Repository, rollup: true);
        Assert.Equal([10ul, 20ul, 25ul], Assert.Single(instrument.Dimensions).Values.Select(value => value.Count));
        AssertPercentiles(instrument, [100, 100, 100]);
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(55, count);
    }

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delta_OmittedStartTimestamp_PreservesChartPlacementAndExport(bool rollup)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext,
                [CreateMetrics(AggregationTemporality.Delta,
                    CreatePoint(DateTime.UnixEpoch, s_start.AddMilliseconds(100), [100, 0, 0], [10, 100]),
                    CreatePoint(DateTime.UnixEpoch, s_start.AddMilliseconds(200), [0, 100, 0], [10, 100]))]);

            Assert.Equal(2, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            await AssertInstrumentAsync(context.Repository);
        }

        using var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        await AssertInstrumentAsync(reopened.Repository);

        async Task AssertInstrumentAsync(ITelemetryRepository repository)
        {
            var instrument = await GetInstrumentAsync(repository, rollup);
            var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
            Assert.Equal(
                [(s_start.AddMilliseconds(100), s_start.AddMilliseconds(100), 100ul),
                 (s_start.AddMilliseconds(200), s_start.AddMilliseconds(200), 100ul)],
                values.Select(value => (value.Start, value.End, value.Count)));
            Assert.All(values, value => Assert.Equal(0ul, value.AggregationStartUnixNano));

            var calculator = new ChartDataCalculator(pointCount: 1, duration: TimeSpan.FromSeconds(1));
            var chartTime = new DateTimeOffset(s_start.AddSeconds(1));
            var countData = calculator.CalculateChartValues(instrument.Dimensions, chartTime, value => value, "Count");
            Assert.Equal([null, 200, null], Assert.Single(countData.Traces).Values);

            var percentileData = calculator.CalculateHistogramValues(instrument.Dimensions, chartTime, value => value, "ms");
            Assert.False(percentileData.HasIncompatibleHistogramBounds);
            Assert.Equal([50, 90, 99], percentileData.Traces.Select(trace => trace.Percentile));
            Assert.Collection(percentileData.Traces,
                trace => Assert.Equal([null, 10, null], trace.Values),
                trace => Assert.Equal([null, 100, null], trace.Values),
                trace => Assert.Equal([null, 100, null], trace.Values));

            var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
            var resource = Assert.Single(exported.ResourceMetrics!);
            var scope = Assert.Single(resource.ScopeMetrics!);
            var histogram = Assert.Single(scope.Metrics!).Histogram;
            Assert.NotNull(histogram);
            Assert.Equal((int)AggregationTemporality.Delta, histogram.AggregationTemporality);
            Assert.All(histogram.DataPoints!, point => Assert.Equal(0ul, point.StartTimeUnixNano));
            Assert.Collection(histogram.DataPoints!,
                point => Assert.Equal(DateTimeToUnixNanoseconds(s_start.AddMilliseconds(100)), point.TimeUnixNano),
                point => Assert.Equal(DateTimeToUnixNanoseconds(s_start.AddMilliseconds(200)), point.TimeUnixNano));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cumulative_SubTickUpdates_RetainsDistinctIntervals(bool sameRequest)
    {
        using var context = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository(maxMetricsCount: 3);
        HistogramDataPoint[] points =
        [
            CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0], [10]),
            CreatePoint(s_start, s_start.AddMilliseconds(200), [20, 0], [10]),
            CreatePoint(s_start, s_start.AddMilliseconds(200), [30, 0], [10]),
            CreatePoint(s_start, s_start.AddMilliseconds(200), [40, 0], [10]),
            CreatePoint(s_start, s_start.AddMilliseconds(300), [50, 0], [10])
        ];
        points[2].TimeUnixNano += 10;
        points[3].TimeUnixNano += 20;
        for (var index = 0; index < points.Length; index++)
        {
            points[index].Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(50), index + 1));
        }

        var addContext = new AddContext();
        if (sameRequest)
        {
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, points)]);
        }
        else
        {
            foreach (var point in points)
            {
                await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, point)]);
            }
        }
        Assert.Equal(5, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);

        var histogram = await GetInstrumentAsync(context.Repository, rollup: false);
        var values = Assert.Single(histogram.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal([30ul, 40ul, 50ul], values.Select(value => value.Count));
        Assert.Equal([points[1].TimeUnixNano, points[2].TimeUnixNano, points[3].TimeUnixNano],
            values.Select(value => value.StartTimeUnixNano));
        Assert.Equal([points[2].TimeUnixNano, points[3].TimeUnixNano, points[4].TimeUnixNano],
            values.Select(value => value.EndTimeUnixNano));
        Assert.Equal([3d, 4d, 5d], values.Select(value => Assert.Single(value.Exemplars).Value));
        using var connection = context.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_points;";
        Assert.Equal(3L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistogramUpsert_MixedOldAndNewIds_CorrelatesPointsAndPreservesRetention(bool rollup)
    {
        using var context = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository(maxMetricsCount: 3);
        var points = Enumerable.Range(0, 4).Select(index =>
        {
            var point = CreatePoint(s_start.AddMilliseconds(index * 100), s_start.AddMilliseconds((index + 1) * 100),
                [100, 0], [10]);
            point.Sum = 500;
            point.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(index * 100 + 50), index + 1));
            return point;
        }).ToArray();
        var addContext = new AddContext();
        await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, points[..3])]);
        Assert.Equal(3, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);

        var metrics = CreateMetrics(AggregationTemporality.Delta, points[3], points[0]);
        metrics.ScopeMetrics[0].Metrics.Add(new Metric
        {
            Name = "gauge",
            Gauge = new Gauge
            {
                DataPoints =
                {
                    new NumberDataPoint
                    {
                        StartTimeUnixNano = DateTimeToUnixNanoseconds(s_start),
                        TimeUnixNano = DateTimeToUnixNanoseconds(s_start.AddMilliseconds(500)),
                        AsInt = 42
                    },
                    new NumberDataPoint
                    {
                        StartTimeUnixNano = DateTimeToUnixNanoseconds(s_start),
                        TimeUnixNano = DateTimeToUnixNanoseconds(s_start.AddMilliseconds(600)),
                        AsInt = 84
                    }
                }
            }
        });
        await context.Repository.AddMetricsAsync(addContext, [metrics]);
        Assert.Equal(7, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);

        var histogram = await GetInstrumentAsync(context.Repository, rollup);
        var values = Assert.Single(histogram.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal(points.Skip(1).Select(point => unchecked((long)point.TimeUnixNano)), values.Select(value => value.AggregationId));
        Assert.Equal([2d, 3d, 4d], values.Select(value => Assert.Single(value.Exemplars).Value));
        Assert.True(ChartDataCalculator.TryCalculatePoint(histogram.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(300, count);

        var gauge = await context.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "gauge",
            StartTime = s_start,
            EndTime = s_start.AddSeconds(1)
        }, CancellationToken.None);
        Assert.NotNull(gauge);
        Assert.Collection(Assert.Single(gauge.Dimensions).Values,
            value => Assert.Equal(42, Assert.IsType<MetricValue<long>>(value).Value),
            value => Assert.Equal(84, Assert.IsType<MetricValue<long>>(value).Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delta_RepeatedInterval_ReplacesPayloadAndPreservesExemplars(bool rollup, bool sameRequest)
    {
        using var context = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository(maxMetricsCount: 1);
        var first = CreatePoint(s_start, s_start.AddMilliseconds(100), [10, 0, 0], [10, 100]);
        first.Sum = 50;
        first.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(50), 5, [KeyValuePair.Create("delivery", "first")]));
        var updated = CreatePoint(s_start, s_start.AddMilliseconds(100), [0, 20, 0], [20, 200]);
        updated.Sum = 1000;
        updated.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(75), 50, [KeyValuePair.Create("delivery", "retry")]));
        var addContext = new AddContext();
        if (sameRequest)
        {
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, first, updated, updated)]);
        }
        else
        {
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, first)]);
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, updated)]);
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, updated)]);
        }
        Assert.Equal(3, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);

        var instrument = await GetInstrumentAsync(context.Repository, rollup);
        var value = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values));
        Assert.Equal(unchecked((long)first.TimeUnixNano), value.AggregationId);
        Assert.Equal(20ul, value.Count);
        Assert.Equal(1000, value.Sum);
        Assert.Equal([0ul, 20ul, 0ul], value.Values);
        Assert.Equal([20d, 200d], value.ExplicitBounds);
        Assert.Collection(value.Exemplars,
            exemplar =>
            {
                Assert.Equal(5, exemplar.Value);
                Assert.Equal(KeyValuePair.Create("delivery", "first"), Assert.Single(exemplar.Attributes));
            },
            exemplar =>
            {
                Assert.Equal(50, exemplar.Value);
                Assert.Equal(KeyValuePair.Create("delivery", "retry"), Assert.Single(exemplar.Attributes));
            });
        AssertPercentiles(instrument, [200, 200, 200]);

        using var connection = context.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_points;";
        Assert.Equal(1L, Assert.IsType<long>(command.ExecuteScalar()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delta_RetryAcrossBatchBoundariesAfterReopening_PreservesRetention(bool rollup)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var options = Options.Create(new DashboardOptions { TelemetryLimits = new TelemetryLimitOptions { MaxMetricsCount = 120 } });
        var points = Enumerable.Range(0, 120).Select(index =>
        {
            var point = CreatePoint(s_start.AddMilliseconds(index), s_start.AddMilliseconds(index + 1), [100, 0], [10]);
            point.Sum = 500;
            return point;
        }).ToArray();
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, dashboardOptions: options))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext,
                [CreateMetrics(AggregationTemporality.Delta, [.. points, .. points.Reverse(), points[0]])]);
            Assert.Equal(241, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
        }

        using var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, dashboardOptions: options);
        var retryContext = new AddContext();
        await reopened.Repository.AddMetricsAsync(retryContext,
            [CreateMetrics(AggregationTemporality.Delta, [.. points.Reverse(), points[0]])]);
        Assert.Equal(121, retryContext.SuccessCount);
        Assert.Equal(0, retryContext.FailureCount);
        var instrument = await GetInstrumentAsync(reopened.Repository, rollup);
        var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal(points.Select(point => unchecked((long)point.TimeUnixNano)), values.Select(value => value.AggregationId));
        Assert.All(values, value => Assert.Equal([100ul, 0ul], value.Values));
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(12000, count);

        using var connection = reopened.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_points;";
        Assert.Equal(120L, Assert.IsType<long>(command.ExecuteScalar()));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Delta_RetriesAtRetentionLimit_PreserveDistinctIntervals(bool rollup, bool sameRequest, bool retryOldest)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var options = Options.Create(new DashboardOptions { TelemetryLimits = new TelemetryLimitOptions { MaxMetricsCount = 3 } });
        var points = new[]
        {
            CreatePoint(s_start, s_start.AddMilliseconds(100), [100, 0, 0], [10, 100]),
            CreatePoint(s_start.AddMilliseconds(100), s_start.AddMilliseconds(200), [0, 100, 0], [10, 100]),
            CreatePoint(s_start.AddMilliseconds(200), s_start.AddMilliseconds(300), [0, 0, 100], [10, 100])
        };
        points[0].Sum = 500;
        points[1].Sum = 5000;
        points[2].Sum = 20000;
        var retry = points[retryOldest ? 0 : 2];
        var next = CreatePoint(s_start.AddMilliseconds(300), s_start.AddMilliseconds(400), [100, 0, 0], [10, 100]);
        next.Sum = 500;
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, dashboardOptions: options))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext,
                [CreateMetrics(AggregationTemporality.Delta, sameRequest ? [.. points, retry, retry] : points)]);
            Assert.Equal(sameRequest ? 5 : 3, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            if (!sameRequest)
            {
                await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, retry)]);
                await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, retry)]);
                Assert.Equal(5, addContext.SuccessCount);
                Assert.Equal(0, addContext.FailureCount);
            }
            await AssertIntervalsAsync(context.Repository, points);
        }

        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, dashboardOptions: options))
        {
            var addContext = new AddContext();
            await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, retry, retry)]);
            Assert.Equal(2, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            await AssertIntervalsAsync(reopened.Repository, points);

            await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, next)]);
            Assert.Equal(3, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            await AssertIntervalsAsync(reopened.Repository, [points[1], points[2], next]);
        }

        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        await AssertIntervalsAsync(readOnly.Repository, [points[1], points[2], next]);

        async Task AssertIntervalsAsync(ITelemetryRepository repository, HistogramDataPoint[] expected)
        {
            var instrument = await GetInstrumentAsync(repository, rollup);
            var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
            Assert.Equal(expected.Select(point => unchecked((long)point.TimeUnixNano)), values.Select(value => value.AggregationId));
            Assert.Equal(expected.Select(point => point.Count), values.Select(value => value.Count));
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].BucketCounts, values[i].Values);
            }
            AssertPercentiles(instrument, [100, 100, 100]);
            Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
            Assert.Equal(300, count);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Delta_SubTickTimestamps_PreservesIntervalsAndDeduplicatesDelivery(bool rollup, bool sameStartTick, bool omitStart)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var timestamp = DateTimeToUnixNanoseconds(s_start);
        var first = CreatePoint(s_start, s_start.AddTicks(2), [100, 0, 0], [10, 100]);
        first.StartTimeUnixNano = omitStart ? 0 : timestamp + (sameStartTick ? 100ul : 0ul);
        first.TimeUnixNano = timestamp + 140;
        var second = CreatePoint(s_start, s_start.AddTicks(2), [0, 100, 0], [10, 100]);
        second.StartTimeUnixNano = omitStart ? 0 : first.TimeUnixNano;
        second.TimeUnixNano = timestamp + 180;
        var points = new[] { first, second };

        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            foreach (var point in points)
            {
                await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, point)]);
            }
            Assert.Equal(2, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            await AssertInstrumentAsync(context.Repository);

            var duplicateContext = new AddContext();
            await context.Repository.AddMetricsAsync(duplicateContext, [CreateMetrics(AggregationTemporality.Delta, points)]);
            Assert.Equal(2, duplicateContext.SuccessCount);
            Assert.Equal(0, duplicateContext.FailureCount);
            await AssertInstrumentAsync(context.Repository);
        }

        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            await AssertInstrumentAsync(reopened.Repository);
            var addContext = new AddContext();
            foreach (var point in points)
            {
                await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Delta, point)]);
            }
            Assert.Equal(2, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            await AssertInstrumentAsync(reopened.Repository);
        }

        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        await AssertInstrumentAsync(readOnly.Repository);

        async Task AssertInstrumentAsync(ITelemetryRepository repository)
        {
            var instrument = await GetInstrumentAsync(repository, rollup);
            var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().OrderBy(value => value.AggregationId).ToArray();
            Assert.Equal([100ul, 100ul], values.Select(value => value.Count));
            Assert.Collection(values,
                value =>
                {
                    Assert.Equal(unchecked((long)first.TimeUnixNano), value.AggregationId);
                    Assert.Equal(omitStart || sameStartTick ? s_start.AddTicks(1) : s_start, value.Start);
                    Assert.Equal([100ul, 0ul, 0ul], value.Values);
                },
                value =>
                {
                    Assert.Equal(unchecked((long)second.TimeUnixNano), value.AggregationId);
                    Assert.Equal(s_start.AddTicks(1), value.Start);
                    Assert.Equal([0ul, 100ul, 0ul], value.Values);
                });
            Assert.All(values, value => Assert.Equal(s_start.AddTicks(1), value.End));
            AssertPercentiles(instrument, [10, 100, 100]);
            Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
            Assert.Equal(200, count);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delta_ExportImport_SubTickTimestamps_PreservesIntervals(bool rollup, bool omitStart)
    {
        using var source = await CreateRepositoryAsync();
        var timestamp = DateTimeToUnixNanoseconds(s_start);
        var first = CreatePoint(s_start, s_start.AddTicks(2), [100, 0, 0], [10, 100]);
        first.StartTimeUnixNano = omitStart ? 0 : timestamp + 110;
        first.TimeUnixNano = timestamp + 140;
        first.Sum = 500;
        var second = CreatePoint(s_start, s_start.AddTicks(2), [0, 100, 0], [10, 100]);
        second.StartTimeUnixNano = omitStart ? 0 : first.TimeUnixNano;
        second.TimeUnixNano = timestamp + 180;
        second.Sum = 5000;
        var addContext = new AddContext();
        await source.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Delta, first, second)]);
        Assert.Equal(2, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);

        var instrument = await GetInstrumentAsync(source.Repository, rollup);
        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
        var exportedPoints = Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!).Histogram!.DataPoints!;
        Assert.Equal([first.StartTimeUnixNano, second.StartTimeUnixNano], exportedPoints.Select(point => point.StartTimeUnixNano));
        var json = JsonSerializer.Serialize(exported, OtlpJsonSerializerContext.DefaultContext.OtlpTelemetryDataJson);
        var deserialized = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.DefaultContext.OtlpTelemetryDataJson);
        Assert.NotNull(deserialized);
        var request = OtlpJsonToProtobufConverter.ToProtobuf(new OtlpExportMetricsServiceRequestJson
        {
            ResourceMetrics = deserialized.ResourceMetrics
        });

        using var destination = await CreateRepositoryAsync();
        var importContext = new AddContext();
        await destination.Repository.AsWriter().AddMetricsAsync(importContext, request.ResourceMetrics);
        Assert.Equal(2, importContext.SuccessCount);
        Assert.Equal(0, importContext.FailureCount);

        var imported = await GetInstrumentAsync(destination.Repository, rollup);
        var values = Assert.Single(imported.Dimensions).Values.Cast<HistogramValue>().OrderBy(value => value.AggregationId).ToArray();
        Assert.Collection(values,
            value =>
            {
                Assert.Equal(unchecked((long)first.TimeUnixNano), value.AggregationId);
                Assert.Equal([100ul, 0ul, 0ul], value.Values);
                Assert.Equal(100ul, value.Count);
            },
            value =>
            {
                Assert.Equal(unchecked((long)second.TimeUnixNano), value.AggregationId);
                Assert.Equal([0ul, 100ul, 0ul], value.Values);
                Assert.Equal(100ul, value.Count);
            });
        AssertPercentiles(imported, [10, 100, 100]);
        Assert.True(ChartDataCalculator.TryCalculatePoint(imported.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(200, count);
    }

    [Theory]
    [InlineData((ulong)long.MaxValue, long.MaxValue)]
    [InlineData((ulong)long.MaxValue + 1, long.MinValue)]
    [InlineData(ulong.MaxValue, -1L)]
    public void Delta_IntervalIdentity_PreservesNanosecondTimestampBits(ulong timestamp, long expectedId)
    {
        var point = CreatePoint(DateTime.UnixEpoch, s_start, [1, 0], [100]);
        point.TimeUnixNano = timestamp;

        var value = HistogramValue.Create(point, OtlpAggregationTemporality.Delta, previous: null);

        Assert.Equal(expectedId, value.AggregationId);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cumulative_ResetAtCountWindowEnd_SelectsPrecedingAggregation(bool rollup)
    {
        using var context = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(AggregationTemporality.Cumulative,
                CreatePoint(s_start, s_start.AddSeconds(1), [100, 0], [100]),
                CreatePoint(s_start.AddSeconds(1), s_start.AddSeconds(2), [1, 0], [100]))]);
        Assert.Equal(2, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        var instrument = await context.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = s_start,
            EndTime = s_start.AddSeconds(2),
            DataPointInterval = rollup ? TimeSpan.FromSeconds(1) : null
        }, CancellationToken.None);
        Assert.NotNull(instrument);
        Assert.Equal(2, Assert.Single(instrument.Dimensions).Values.Count);

        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddSeconds(1), out var count));
        Assert.Equal(100, count);
        Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start.AddSeconds(1), s_start.AddSeconds(2), out count));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 1)]
    [InlineData(3, 5)]
    public async Task Cumulative_NonAlignedResetRollup_PreservesBoundaryAndExemplars(int resetKind, int intervalSeconds)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var boundary = s_start.AddMilliseconds(3500);
        var producerStart = resetKind == 3 ? DateTime.UnixEpoch : s_start.AddMilliseconds(1500);
        var resetProducerStart = resetKind == 0 ? boundary : producerStart;
        var first = CreatePoint(producerStart, boundary, [100, 0], [10]);
        var reset = CreatePoint(resetProducerStart, s_start.AddMilliseconds(3700),
            resetKind switch { 0 => [101, 0], 2 => [0, 101], _ => [1, 0] }, [10]);
        var continuation = CreatePoint(resetProducerStart, s_start.AddMilliseconds(3900),
            resetKind switch { 0 => [102, 0], 2 => [0, 102], _ => [2, 0] }, [10]);
        first.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(3250), 100, [KeyValuePair.Create("phase", "before")]));
        reset.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(3600), 1, [KeyValuePair.Create("phase", "reset")]));
        continuation.Exemplars.Add(CreateExemplar(s_start.AddMilliseconds(3800), 2, [KeyValuePair.Create("phase", "continuation")]));

        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, first, reset)]);
            Assert.Equal(2, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
        }
        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, continuation)]);
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            await AssertInstrumentAsync(reopened.Repository);
        }
        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        await AssertInstrumentAsync(readOnly.Repository);

        async Task AssertInstrumentAsync(ITelemetryRepository repository)
        {
            var raw = await GetInstrumentAsync(repository, rollup: false);
            Assert.Equal([producerStart, boundary, s_start.AddMilliseconds(3700)],
                Assert.Single(raw.Dimensions).Values.Select(value => value.Start));
            Assert.Equal([100d, 1d, 2d], Assert.Single(raw.Dimensions).Values.Select(value => Assert.Single(value.Exemplars).Value));
            var request = new GetInstrumentRequest
            {
                ResourceKey = new ResourceKey("TestService", "TestId"),
                MeterName = "test-meter",
                InstrumentName = "histogram",
                StartTime = s_start.AddMilliseconds(1500),
                EndTime = s_start.AddSeconds(5),
                DataPointInterval = TimeSpan.FromSeconds(intervalSeconds)
            };
            var instrument = await repository.GetInstrumentAsync(request, CancellationToken.None);
            Assert.NotNull(instrument);
            var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
            Assert.Equal(2, values.Length);
            Assert.Equal(boundary, values[1].Start);
            Assert.Equal(s_start.AddMilliseconds(3900), values[1].End);
            Assert.Equal(continuation.Count, values[1].Count);
            Assert.NotEqual(values[0].AggregationId, values[1].AggregationId);
            Assert.Equal(DateTimeToUnixNanoseconds(resetProducerStart), values[1].AggregationStartUnixNano);
            Assert.Equal(100, Assert.Single(values[0].Exemplars).Value);
            Assert.Equal([1d, 2d], values[1].Exemplars.Select(exemplar => exemplar.Value));
            Assert.Equal(["reset", "continuation"], values[1].Exemplars.Select(exemplar => Assert.Single(exemplar.Attributes).Value));
            Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start.AddMilliseconds(1500), boundary, out var count));
            Assert.Equal(100, count);
            Assert.True(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, boundary, s_start.AddSeconds(5), out count));
            Assert.Equal((double)continuation.Count, count);
            var countChart = new ChartDataCalculator(pointCount: 1, duration: TimeSpan.FromSeconds(2))
                .CalculateChartValues(instrument.Dimensions, new DateTimeOffset(boundary), value => value, "Count");
            Assert.Equal([resetKind == 3 ? 100d : (double?)null, 100d, (double)continuation.Count],
                Assert.Single(countChart.Traces).Values);

            var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
            var metric = Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!);
            Assert.Equal(DateTimeToUnixNanoseconds(resetProducerStart), metric.Histogram!.DataPoints![1].StartTimeUnixNano);

            var cursors = MetricInstrumentDataCache.CreateCursors(instrument, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
            var refreshed = await repository.GetInstrumentAsync(new GetInstrumentRequest
            {
                ResourceKey = request.ResourceKey,
                MeterName = request.MeterName,
                InstrumentName = request.InstrumentName,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                DataPointInterval = request.DataPointInterval,
                DimensionCursors = cursors
            }, CancellationToken.None);
            Assert.NotNull(refreshed);
            var merged = MetricInstrumentDataCache.Merge(instrument, refreshed, cursors, request.StartTime!.Value);
            var mergedValues = Assert.Single(merged.Dimensions).Values.Cast<HistogramValue>().ToArray();
            Assert.Equal(values.Select(value => (value.Start, value.End, value.Count, value.AggregationId)),
                mergedValues.Select(value => (value.Start, value.End, value.Count, value.AggregationId)));
            Assert.Equal([1d, 2d], mergedValues[1].Exemplars.Select(exemplar => exemplar.Value));

            var partial = await repository.GetInstrumentAsync(new GetInstrumentRequest
            {
                ResourceKey = request.ResourceKey,
                MeterName = request.MeterName,
                InstrumentName = request.InstrumentName,
                StartTime = s_start.AddMilliseconds(3800),
                EndTime = request.EndTime,
                DataPointInterval = request.DataPointInterval
            }, CancellationToken.None);
            Assert.NotNull(partial);
            var partialValue = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(partial.Dimensions).Values));
            Assert.Equal(s_start.AddMilliseconds(3700), partialValue.Start);
            Assert.Equal(2, Assert.Single(partialValue.Exemplars).Value);
            Assert.False(ChartDataCalculator.TryCalculatePoint(partial.Dimensions, s_start, boundary, out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cumulative_NonAlignedResetRollup_AfterTrimming_DoesNotMoveHistoryBackwards(bool omitStart)
    {
        using var context = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository(maxMetricsCount: 2);
        var producerStart = omitStart ? DateTime.UnixEpoch : s_start;
        var resetStart = omitStart ? DateTime.UnixEpoch : s_start.AddMilliseconds(3500);
        var addContext = new AddContext();
        await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative,
            CreatePoint(producerStart, s_start.AddMilliseconds(3500), [100, 0], [10]),
            CreatePoint(resetStart, s_start.AddMilliseconds(3700), [1, 0], [10]),
            CreatePoint(resetStart, s_start.AddMilliseconds(3900), [2, 0], [10]),
            CreatePoint(resetStart, s_start.AddMilliseconds(4100), [3, 0], [10]))]);
        Assert.Equal(4, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);
        var instrument = await context.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = s_start,
            EndTime = s_start.AddSeconds(5),
            DataPointInterval = TimeSpan.FromSeconds(1)
        }, CancellationToken.None);
        Assert.NotNull(instrument);
        var snapshot = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values));
        Assert.Equal(s_start.AddMilliseconds(3700), snapshot.Start);
        Assert.Equal(3UL, snapshot.Count);
        Assert.False(ChartDataCalculator.TryCalculatePoint(instrument.Dimensions, s_start, s_start.AddMilliseconds(3500), out _));
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
        Assert.Equal(DateTimeToUnixNanoseconds(resetStart), values[1].AggregationStartUnixNano);
        Assert.Equal(explicitStart ? resetStart : s_start.AddMilliseconds(200), values[1].Start);
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Cumulative_SubTickReset_PreservesAggregationsAfterReopening(bool rollup, bool sameStartTick, bool nonDecreasingBuckets)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var timestamp = DateTimeToUnixNanoseconds(s_start);
        var first = CreatePoint(s_start, s_start.AddMilliseconds(500), [1, 0, 0], [10, 100]);
        first.StartTimeUnixNano = timestamp + (sameStartTick ? 500_000_000ul : 0ul);
        first.TimeUnixNano = timestamp + 500_000_010;
        first.Sum = 5;
        var reset = CreatePoint(s_start, s_start.AddMilliseconds(500), nonDecreasingBuckets ? [1, 1, 0] : [0, 1, 0], [10, 100]);
        reset.StartTimeUnixNano = timestamp + 500_000_020;
        reset.TimeUnixNano = timestamp + 500_000_080;
        reset.Sum = 20;
        long firstId;
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, first)]);
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            var instrument = await GetInstrumentAsync(context.Repository, rollup: false);
            firstId = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument.Dimensions).Values)).AggregationId;
        }

        long resetId;
        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, reset)]);
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
            var instrument = await GetInstrumentAsync(reopened.Repository, rollup);
            var values = Assert.Single(instrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
            Assert.Collection(values,
                value =>
                {
                    Assert.Equal(firstId, value.AggregationId);
                    Assert.Equal([1ul, 0ul, 0ul], value.Values);
                },
                value =>
                {
                    Assert.NotEqual(firstId, value.AggregationId);
                    Assert.Equal(reset.BucketCounts, value.Values);
                });
            resetId = values[1].AggregationId;
            AssertPercentiles(instrument, [10, 100, 100]);
            var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
            var exportedPoints = Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!).Histogram!.DataPoints!;
            Assert.Equal([first.StartTimeUnixNano, reset.StartTimeUnixNano], exportedPoints.Select(point => point.StartTimeUnixNano));
            Assert.Equal([first.TimeUnixNano, reset.TimeUnixNano], exportedPoints.Select(point => point.TimeUnixNano));
        }

        using (var reopened = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var continued = reset.Clone();
            continued.TimeUnixNano = timestamp + 500_000_090;
            continued.BucketCounts[1] = 2;
            continued.Count = nonDecreasingBuckets ? 3ul : 2ul;
            continued.Sum = 40;
            var addContext = new AddContext();
            await reopened.Repository.AddMetricsAsync(addContext, [CreateMetrics(AggregationTemporality.Cumulative, continued)]);
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
        }

        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        var finalInstrument = await GetInstrumentAsync(readOnly.Repository, rollup);
        var finalValues = Assert.Single(finalInstrument.Dimensions).Values.Cast<HistogramValue>().ToArray();
        ulong[] expectedCounts = rollup
            ? [1ul, nonDecreasingBuckets ? 3ul : 2ul]
            : [1ul, reset.Count, nonDecreasingBuckets ? 3ul : 2ul];
        Assert.Equal(expectedCounts, finalValues.Select(value => value.Count));
        Assert.Equal(firstId, finalValues[0].AggregationId);
        Assert.All(finalValues.Skip(1), value => Assert.Equal(resetId, value.AggregationId));
        AssertPercentiles(finalInstrument, [nonDecreasingBuckets ? 10 : 100, 100, 100]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Cumulative_SubTickStartsWithNonDecreasingCounts_DetectsReset(bool unchangedValues, bool changedLayout)
    {
        var first = CreatePoint(s_start, s_start, [1, 0], [100]);
        first.StartTimeUnixNano += 10;
        first.TimeUnixNano += 20;
        var reset = first.Clone();
        reset.StartTimeUnixNano += 20;
        reset.TimeUnixNano += 60;
        if (!unchangedValues)
        {
            reset.BucketCounts[0]++;
            reset.Count++;
            reset.Sum++;
        }
        if (changedLayout)
        {
            reset.ExplicitBounds[0] = 200;
        }

        var previous = HistogramValue.Create(first, OtlpAggregationTemporality.Cumulative, previous: null);
        var current = HistogramValue.Create(reset, OtlpAggregationTemporality.Cumulative, previous);

        Assert.NotSame(previous, current);
        Assert.NotEqual(previous.AggregationId, current.AggregationId);
        Assert.Equal(reset.BucketCounts, current.Values);
        Assert.Equal(reset.ExplicitBounds, current.ExplicitBounds);
        Assert.Equal(reset.StartTimeUnixNano, current.AggregationStartUnixNano);
        Assert.Equal(reset.TimeUnixNano, current.EndTimeUnixNano);
        Assert.Equal(s_start, current.Start);
        Assert.Equal(s_start, current.End);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Histogram_ExportImport_SubTickStartsAndEnds_PreservesAggregations(bool cumulative, bool rollup)
    {
        using var source = await CreateRepositoryAsync();
        var first = CreatePoint(s_start, s_start, [1, 0, 0], [10, 100]);
        first.StartTimeUnixNano += 10;
        first.TimeUnixNano += 20;
        var second = CreatePoint(s_start, s_start, [1, 1, 0], [10, 100]);
        second.StartTimeUnixNano += 30;
        second.TimeUnixNano += 80;
        var temporality = cumulative ? AggregationTemporality.Cumulative : AggregationTemporality.Delta;
        var addContext = new AddContext();
        await source.Repository.AsWriter().AddMetricsAsync(addContext, [CreateMetrics(temporality, first, second)]);
        Assert.Equal(2, addContext.SuccessCount);
        Assert.Equal(0, addContext.FailureCount);

        var instrument = await GetInstrumentAsync(source.Repository, rollup);
        var cursors = MetricInstrumentDataCache.CreateCursors(instrument, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        var cloned = MetricInstrumentDataCache.Merge(instrument, instrument, cursors, s_start);
        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([cloned]);
        var exportedPoints = Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!).Histogram!.DataPoints!;
        Assert.Equal([(first.StartTimeUnixNano, first.TimeUnixNano), (second.StartTimeUnixNano, second.TimeUnixNano)],
            exportedPoints.Select(point => (point.StartTimeUnixNano!.Value, point.TimeUnixNano!.Value)));
        var json = JsonSerializer.Serialize(exported, OtlpJsonSerializerContext.DefaultContext.OtlpTelemetryDataJson);
        var deserialized = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.DefaultContext.OtlpTelemetryDataJson);
        Assert.NotNull(deserialized);
        var request = OtlpJsonToProtobufConverter.ToProtobuf(new OtlpExportMetricsServiceRequestJson
        {
            ResourceMetrics = deserialized.ResourceMetrics
        });

        using var destination = await CreateRepositoryAsync();
        var importContext = new AddContext();
        await destination.Repository.AsWriter().AddMetricsAsync(importContext, request.ResourceMetrics);
        Assert.Equal(2, importContext.SuccessCount);
        Assert.Equal(0, importContext.FailureCount);
        var imported = await GetInstrumentAsync(destination.Repository, rollup);
        var values = Assert.Single(imported.Dimensions).Values.Cast<HistogramValue>().ToArray();
        Assert.Equal([1ul, 2ul], values.Select(value => value.Count));
        Assert.NotEqual(values[0].AggregationId, values[1].AggregationId);
        AssertPercentiles(imported, [10, 100, 100]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Histogram_ReopenedDatabase_PreservesMaximumSignedNanosecondTimestamp(bool cumulative)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, "dashboard.db");
        var end = (ulong)long.MaxValue;
        var start = end - 200;
        var point = CreatePoint(s_start, s_start, [1, 0], [100]);
        point.StartTimeUnixNano = start;
        point.TimeUnixNano = end;
        using (var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath))
        {
            var addContext = new AddContext();
            await context.Repository.AddMetricsAsync(addContext,
                [CreateMetrics(cumulative ? AggregationTemporality.Cumulative : AggregationTemporality.Delta, point)]);
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
        }

        using var readOnly = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(databasePath, readOnly: true);
        var instrument = await readOnly.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "test-meter",
            InstrumentName = "histogram",
            StartTime = OtlpHelpers.UnixNanoSecondsToDateTime(start),
            EndTime = readOnly.Repository.GetInstrumentLatestEndTime(new ResourceKey("TestService", "TestId"), "test-meter", "histogram")
        }, CancellationToken.None);
        Assert.NotNull(instrument);
        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
        var exportedPoint = Assert.Single(Assert.Single(Assert.Single(Assert.Single(exported.ResourceMetrics!).ScopeMetrics!).Metrics!).Histogram!.DataPoints!);
        Assert.Equal(start, exportedPoint.StartTimeUnixNano);
        Assert.Equal(end, exportedPoint.TimeUnixNano);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Histogram_OutOfRangeNanosecondTimestamp_RejectsPointBeforeDimensionCreation(bool cumulative, bool invalidStart)
    {
        using var context = await CreateRepositoryAsync();
        var point = CreatePoint(s_start, s_start.AddSeconds(1), [1, 0], [100]);
        if (invalidStart)
        {
            point.StartTimeUnixNano = (ulong)long.MaxValue + 1;
        }
        else
        {
            point.TimeUnixNano = (ulong)long.MaxValue + 1;
        }
        var addContext = new AddContext();
        await context.Repository.AsWriter().AddMetricsAsync(addContext,
            [CreateMetrics(cumulative ? AggregationTemporality.Cumulative : AggregationTemporality.Delta, point)]);

        Assert.Equal(0, addContext.SuccessCount);
        Assert.Equal(1, addContext.FailureCount);
        Assert.Empty((await GetInstrumentAsync(context.Repository, rollup: false)).Dimensions);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(70ul)]
    public void Cumulative_ResetsWithEqualOrSubTickEnds_HaveDistinctIdentities(ulong endOffset)
    {
        var first = CreatePoint(s_start, s_start.AddMilliseconds(500), [1, 0], [100]);
        first.TimeUnixNano += 10;
        first.Sum = 50;
        var reset = first.Clone();
        reset.TimeUnixNano += endOffset;
        reset.BucketCounts[0] = 0;
        reset.BucketCounts[1] = 1;
        reset.Sum = 200;
        var nextReset = first.Clone();
        nextReset.TimeUnixNano = reset.TimeUnixNano;

        var firstValue = HistogramValue.Create(first, OtlpAggregationTemporality.Cumulative, previous: null);
        var resetValue = HistogramValue.Create(reset, OtlpAggregationTemporality.Cumulative, firstValue);
        var nextResetValue = HistogramValue.Create(nextReset, OtlpAggregationTemporality.Cumulative, resetValue);

        Assert.Equal(3, new[] { firstValue.AggregationId, resetValue.AggregationId, nextResetValue.AggregationId }.Distinct().Count());
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
        Assert.Equal(DateTimeToUnixNanoseconds(s_start), values[1].AggregationStartUnixNano);

        var exported = TelemetryExportService.ConvertMetricsToOtlpJson([instrument]);
        var resource = Assert.Single(exported.ResourceMetrics!);
        var scope = Assert.Single(resource.ScopeMetrics!);
        var histogram = Assert.Single(scope.Metrics!).Histogram;
        Assert.NotNull(histogram);
        Assert.Equal((int)AggregationTemporality.Cumulative, histogram.AggregationTemporality);
        Assert.Collection(histogram.DataPoints!,
            point =>
            {
                Assert.Equal(DateTimeToUnixNanoseconds(s_start), point.StartTimeUnixNano);
                Assert.Equal(DateTimeToUnixNanoseconds(s_start.AddMilliseconds(100)), point.TimeUnixNano);
            },
            point =>
            {
                Assert.Equal(DateTimeToUnixNanoseconds(s_start), point.StartTimeUnixNano);
                Assert.Equal(DateTimeToUnixNanoseconds(s_start.AddMilliseconds(200)), point.TimeUnixNano);
            });
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
            s_start, s_start.AddSeconds(1), traces, [], out var incompatibleBounds));
        Assert.False(incompatibleBounds);
        Assert.Equal(expected, traces.Values.Select(trace => Assert.Single(trace.Values)));
    }
}
