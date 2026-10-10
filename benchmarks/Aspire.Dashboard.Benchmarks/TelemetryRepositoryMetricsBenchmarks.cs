// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(Config))]
public class TelemetryRepositoryMetricsBenchmarks
{
    private const int MetricSamplesPerBatch = 100;
    private const string MetricMeterName = "benchmark-meter";
    private const string MetricInstrumentName = "benchmark.metric";
    private const string HistogramMetricInstrumentName = "benchmark.histogram";
    private static readonly double[] s_histogramObservations = [5, 25, 75, 150];
    private static readonly TimeSpan s_metricDataDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_metricDisplayDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_metricInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_metricExemplarInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_metricDataPointInterval = MetricDataPointInterval.Get(s_metricDisplayDuration);
    private static readonly TimeSpan s_metricHistoryDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, s_metricDataPointInterval.Ticks));
    private static readonly ResourceKey s_metricResourceKey = new("benchmark-app", "benchmark-instance");

    private string _temporaryDirectory = null!;
    private DashboardSqliteDatabase _database = null!;
    private SqliteTelemetryRepository _queryRepository = null!;
    private IReadOnlyList<MetricDimensionCursor> _incrementalCursors = null!;
    private RepeatedField<ResourceMetrics> _ingestionMetrics = null!;
    private HistogramDataPoint[] _ingestionPoints = null!;

    [Params(1, 5)]
    public int DimensionCount { get; set; }

    [GlobalSetup(Target = nameof(GetMetricsLongDuration))]
    public Task SetupMetrics() => SetupAsync(isHistogram: false);

    [GlobalSetup(Target = nameof(GetHistogramMetricsLongDuration))]
    public Task SetupHistogramMetrics() => SetupAsync(isHistogram: true);

    [GlobalSetup(Target = nameof(GetMetricsLongDurationRollup))]
    public Task SetupMetricsRollup() => SetupAsync(isHistogram: false);

    [GlobalSetup(Target = nameof(GetHistogramMetricsLongDurationRollup))]
    public Task SetupHistogramMetricsRollup() => SetupAsync(isHistogram: true);

    [GlobalSetup(Target = nameof(GetMetricsIncrementalRollup))]
    public Task SetupMetricsIncrementalRollup() => SetupIncrementalAsync(isHistogram: false, MetricInstrumentName);

    [GlobalSetup(Target = nameof(GetHistogramMetricsIncrementalRollup))]
    public Task SetupHistogramMetricsIncrementalRollup() => SetupIncrementalAsync(isHistogram: true, HistogramMetricInstrumentName);

    [GlobalSetup(Target = nameof(AddHistogramMetricsAtCapacity))]
    public async Task SetupHistogramMetricsIngestion()
    {
        await InitializeRepositoryAsync();
        var retainedPointCount = new TelemetryLimitOptions().MaxMetricsCount;
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var exemplars = s_histogramObservations.Select(value => CreateMetricExemplar(startTime, value)).ToArray();
        var context = new AddContext();
        foreach (var batch in CreateLongDurationMetricBatches(DimensionCount, isHistogram: true, retainedPointCount))
        {
            foreach (var point in batch[0].ScopeMetrics[0].Metrics[0].Histogram.DataPoints)
            {
                // Cumulative reservoirs replay unrefreshed exemplars. Include them in every export to
                // exercise point eviction and its cascading exemplar deletes in a long-running app.
                point.Exemplars.Clear();
                point.Exemplars.Add(exemplars);
            }
            await _queryRepository.AddMetricsAsync(context, batch);
            _ingestionMetrics = batch;
        }
        if (context.FailureCount > 0)
        {
            throw new InvalidOperationException($"Failed to add {context.FailureCount} benchmark metric points.");
        }

        var points = _ingestionMetrics[0].ScopeMetrics[0].Metrics[0].Histogram.DataPoints;
        _ingestionPoints = points.TakeLast(DimensionCount).ToArray();
        points.Clear();
        points.Add(_ingestionPoints);
    }

    private async Task InitializeRepositoryAsync()
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-metrics-benchmark-").FullName;
        _database = new DashboardSqliteDatabase(Path.Combine(_temporaryDirectory, "query.db"));
        await _database.InitializeSchemaAsync(CancellationToken.None);
        _queryRepository = CreateRepository(_database);
    }

    private async Task SetupAsync(bool isHistogram)
    {
        await InitializeRepositoryAsync();
        var addContext = new AddContext();
        foreach (var batch in CreateLongDurationMetricBatches(DimensionCount, isHistogram, (int)(s_metricDataDuration / s_metricInterval)))
        {
            await _queryRepository.AddMetricsAsync(addContext, batch);
        }
        if (addContext.FailureCount > 0)
        {
            throw new InvalidOperationException($"Failed to add {addContext.FailureCount} benchmark metric points.");
        }
    }

    private async Task SetupIncrementalAsync(bool isHistogram, string instrumentName)
    {
        await SetupAsync(isHistogram);
        var instrument = await GetLongDurationInstrumentAsync(instrumentName, s_metricDataPointInterval);
        _incrementalCursors = instrument.Dimensions.Select(dimension =>
        {
            var latestValue = dimension.Values[^1];
            return new MetricDimensionCursor
            {
                Attributes = dimension.Attributes,
                StartTime = latestValue.End.Subtract(s_metricDataPointInterval)
            };
        }).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queryRepository.Dispose();
        _database.ClearPool();
        _database.Dispose();
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Benchmark(Description = "TelemetryRepository: query 6h metrics display")]
    public async Task<int> GetMetricsLongDuration()
    {
        var instrument = await GetLongDurationInstrumentAsync(MetricInstrumentName);

        return instrument.Dimensions.Sum(dimension => dimension.Values.Count);
    }

    [Benchmark(Description = "TelemetryRepository: query 6h histogram metrics display")]
    public async Task<int> GetHistogramMetricsLongDuration()
    {
        var instrument = await GetLongDurationInstrumentAsync(HistogramMetricInstrumentName);

        return instrument.Dimensions.Sum(dimension =>
            dimension.Values.Count + dimension.Values.Sum(value => value.Exemplars.Count));
    }

    [Benchmark(Description = "TelemetryRepository: query 6h metrics with dashboard rollup")]
    public async Task<int> GetMetricsLongDurationRollup()
    {
        var instrument = await GetLongDurationInstrumentAsync(MetricInstrumentName, s_metricDataPointInterval);

        return instrument.Dimensions.Sum(dimension => dimension.Values.Count);
    }

    [Benchmark(Description = "TelemetryRepository: query 6h histogram metrics with dashboard rollup")]
    public async Task<int> GetHistogramMetricsLongDurationRollup()
    {
        var instrument = await GetLongDurationInstrumentAsync(HistogramMetricInstrumentName, s_metricDataPointInterval);

        return instrument.Dimensions.Sum(dimension =>
            dimension.Values.Count + dimension.Values.Sum(value => value.Exemplars.Count));
    }

    [Benchmark(Description = "TelemetryRepository: query incremental metrics with dashboard rollup")]
    public async Task<int> GetMetricsIncrementalRollup()
    {
        var instrument = await GetLongDurationInstrumentAsync(MetricInstrumentName, s_metricDataPointInterval, _incrementalCursors);

        return instrument.Dimensions.Sum(dimension => dimension.Values.Count);
    }

    [Benchmark(Description = "TelemetryRepository: query incremental histogram metrics with dashboard rollup")]
    public async Task<int> GetHistogramMetricsIncrementalRollup()
    {
        var instrument = await GetLongDurationInstrumentAsync(HistogramMetricInstrumentName, s_metricDataPointInterval, _incrementalCursors);

        return instrument.Dimensions.Sum(dimension =>
            dimension.Values.Count + dimension.Values.Sum(value => value.Exemplars.Count));
    }

    [Benchmark(Description = "TelemetryRepository: ingest histogram metrics at retention capacity")]
    public async Task<int> AddHistogramMetricsAtCapacity()
    {
        foreach (var point in _ingestionPoints)
        {
            point.Count++;
            point.Sum += s_histogramObservations[0];
            point.BucketCounts[0]++;
            point.TimeUnixNano += (ulong)s_metricInterval.Ticks * 100;
        }

        var context = new AddContext();
        await _queryRepository.AddMetricsAsync(context, _ingestionMetrics);
        if (context.SuccessCount != DimensionCount || context.FailureCount > 0)
        {
            throw new InvalidOperationException($"Expected {DimensionCount} benchmark metric points, added {context.SuccessCount} and rejected {context.FailureCount}.");
        }

        return context.SuccessCount;
    }

    private async Task<OtlpInstrumentData> GetLongDurationInstrumentAsync(
        string instrumentName,
        TimeSpan? dataPointInterval = null,
        IReadOnlyList<MetricDimensionCursor>? dimensionCursors = null)
    {
        var endTime = _queryRepository.GetInstrumentLatestEndTime(s_metricResourceKey, MetricMeterName, instrumentName)
            ?? throw new InvalidOperationException($"Unable to find the benchmark metric '{instrumentName}' end time.");

        // Match the dashboard metrics display query, which includes one preceding rollup for histogram calculations.
        return await _queryRepository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = s_metricResourceKey,
            MeterName = MetricMeterName,
            InstrumentName = instrumentName,
            StartTime = endTime.Subtract(s_metricDisplayDuration + s_metricHistoryDuration),
            EndTime = endTime,
            DataPointInterval = dataPointInterval,
            PopulateExemplarAttributes = false,
            DimensionCursors = dimensionCursors ?? []
        }, cancellationToken: CancellationToken.None) ?? throw new InvalidOperationException($"Unable to find the benchmark metric '{instrumentName}'.");
    }

    private static SqliteTelemetryRepository CreateRepository(DashboardSqliteDatabase database)
    {
        return new SqliteTelemetryRepository(
            database,
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions()),
            new PauseManager(),
            TimeProvider.System,
            []);
    }

    private static IEnumerable<RepeatedField<ResourceMetrics>> CreateLongDurationMetricBatches(int dimensionCount, bool isHistogram, int totalSampleCount)
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var observations = s_histogramObservations;
        var bucketCounts = Enumerable.Range(0, dimensionCount).Select(_ => new ulong[observations.Length]).ToArray();
        var sums = new double[dimensionCount];

        for (var firstSampleIndex = 0; firstSampleIndex < totalSampleCount; firstSampleIndex += MetricSamplesPerBatch)
        {
            var sampleCount = Math.Min(MetricSamplesPerBatch, totalSampleCount - firstSampleIndex);
            yield return CreateLongDurationMetrics(
                startTime,
                dimensionCount,
                firstSampleIndex,
                sampleCount,
                observations,
                bucketCounts,
                sums,
                isHistogram);
        }
    }

    private static RepeatedField<ResourceMetrics> CreateLongDurationMetrics(
        DateTime startTime,
        int dimensionCount,
        int firstSampleIndex,
        int sampleCount,
        double[] observations,
        ulong[][] bucketCounts,
        double[] sums,
        bool isHistogram)
    {
        return
        [
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = new InstrumentationScope { Name = MetricMeterName },
                        Metrics =
                        {
                            CreateMetric(startTime, dimensionCount, firstSampleIndex, sampleCount, observations, bucketCounts, sums, isHistogram)
                        }
                    }
                }
            }
        ];
    }

    private static Metric CreateMetric(
        DateTime startTime,
        int dimensionCount,
        int firstSampleIndex,
        int sampleCount,
        double[] observations,
        ulong[][] bucketCounts,
        double[] sums,
        bool isHistogram)
    {
        return isHistogram
            ? new Metric
            {
                Name = HistogramMetricInstrumentName,
                Description = "Long-running benchmark histogram metric",
                Unit = "ms",
                Histogram = new Histogram
                {
                    AggregationTemporality = AggregationTemporality.Cumulative,
                    DataPoints = { CreateHistogramMetricPoints(startTime, dimensionCount, firstSampleIndex, sampleCount, observations, bucketCounts, sums) }
                }
            }
            : new Metric
            {
                Name = MetricInstrumentName,
                Description = "Long-running benchmark metric",
                Unit = "requests",
                Sum = new Sum
                {
                    AggregationTemporality = AggregationTemporality.Cumulative,
                    IsMonotonic = true,
                    DataPoints = { CreateMetricPoints(startTime, dimensionCount, firstSampleIndex, sampleCount) }
                }
            };
    }

    private static IEnumerable<NumberDataPoint> CreateMetricPoints(
        DateTime startTime,
        int dimensionCount,
        int firstSampleIndex,
        int sampleCount)
    {
        for (var sampleOffset = 0; sampleOffset < sampleCount; sampleOffset++)
        {
            var sampleIndex = firstSampleIndex + sampleOffset;
            var pointTime = startTime.AddTicks(s_metricInterval.Ticks * sampleIndex);
            for (var dimensionIndex = 0; dimensionIndex < dimensionCount; dimensionIndex++)
            {
                yield return new NumberDataPoint
                {
                    AsInt = sampleIndex,
                    StartTimeUnixNano = DateTimeToUnixNanoseconds(pointTime),
                    TimeUnixNano = DateTimeToUnixNanoseconds(pointTime),
                    Attributes = { CreateDimensionAttribute(dimensionIndex) }
                };
            }
        }
    }

    private static IEnumerable<HistogramDataPoint> CreateHistogramMetricPoints(
        DateTime startTime,
        int dimensionCount,
        int firstSampleIndex,
        int sampleCount,
        double[] observations,
        ulong[][] bucketCounts,
        double[] sums)
    {
        for (var sampleOffset = 0; sampleOffset < sampleCount; sampleOffset++)
        {
            var sampleIndex = firstSampleIndex + sampleOffset;
            var pointTime = startTime.AddTicks(s_metricInterval.Ticks * sampleIndex);
            var observationIndex = sampleIndex % observations.Length;
            var observation = observations[observationIndex];
            for (var dimensionIndex = 0; dimensionIndex < dimensionCount; dimensionIndex++)
            {
                bucketCounts[dimensionIndex][observationIndex]++;
                sums[dimensionIndex] += observation;

                var point = new HistogramDataPoint
                {
                    Count = checked((ulong)sampleIndex + 1),
                    Sum = sums[dimensionIndex],
                    StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
                    TimeUnixNano = DateTimeToUnixNanoseconds(pointTime),
                    ExplicitBounds = { 10, 50, 100 },
                    Attributes = { CreateDimensionAttribute(dimensionIndex) }
                };
                point.BucketCounts.Add(bucketCounts[dimensionIndex]);

                if ((pointTime - startTime).Ticks % s_metricExemplarInterval.Ticks == 0)
                {
                    point.Exemplars.Add(CreateMetricExemplar(pointTime, observation));
                }

                yield return point;
            }
        }
    }

    private static KeyValue CreateDimensionAttribute(int dimensionIndex)
    {
        return new KeyValue
        {
            Key = "benchmark.dimension",
            Value = new AnyValue { StringValue = dimensionIndex.ToString(CultureInfo.InvariantCulture) }
        };
    }

    private static Exemplar CreateMetricExemplar(DateTime pointTime, double value)
    {
        return new Exemplar
        {
            TimeUnixNano = DateTimeToUnixNanoseconds(pointTime),
            AsDouble = value,
            SpanId = ByteString.CopyFromUtf8("span-id0"),
            TraceId = ByteString.CopyFromUtf8("trace-id00000000")
        };
    }

    private static Resource CreateResource()
    {
        return new Resource
        {
            Attributes =
            {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "benchmark-app" } },
                new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "benchmark-instance" } }
            }
        };
    }

    private static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timeSinceEpoch = dateTime.ToUniversalTime() - unixEpoch;

        return (ulong)timeSinceEpoch.Ticks * 100;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Dry.WithToolchain(InProcessNoEmitToolchain.Instance).DontEnforcePowerPlan());

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}