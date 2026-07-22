// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(Config))]
public class TelemetryRepositoryMetricsBenchmarks
{
    private const int MetricSamplesPerBatch = 100;
    private const string MetricMeterName = "benchmark-meter";
    private const string MetricInstrumentName = "benchmark.metric";
    private const string HistogramMetricInstrumentName = "benchmark.histogram";
    private static readonly TimeSpan s_metricDataDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan s_metricDisplayDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan s_metricInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_metricExemplarInterval = TimeSpan.FromSeconds(10);
    private static readonly ResourceKey s_metricResourceKey = new("benchmark-app", "benchmark-instance");

    private string _temporaryDirectory = null!;
    private SqliteTelemetryRepository _queryRepository = null!;
    private IReadOnlyList<MetricDimensionCursor> _incrementalCursors = null!;

    [Params(1, 10)]
    public int DimensionCount { get; set; }

    [GlobalSetup(Target = nameof(GetMetricsLongDuration))]
    public void SetupMetrics() => Setup(isHistogram: false);

    [GlobalSetup(Target = nameof(GetHistogramMetricsLongDuration))]
    public void SetupHistogramMetrics() => Setup(isHistogram: true);

    [GlobalSetup(Target = nameof(GetMetricsLongDurationRollup))]
    public void SetupMetricsRollup() => Setup(isHistogram: false);

    [GlobalSetup(Target = nameof(GetHistogramMetricsLongDurationRollup))]
    public void SetupHistogramMetricsRollup() => Setup(isHistogram: true);

    [GlobalSetup(Target = nameof(GetMetricsIncrementalRollup))]
    public void SetupMetricsIncrementalRollup() => SetupIncremental(isHistogram: false, MetricInstrumentName);

    [GlobalSetup(Target = nameof(GetHistogramMetricsIncrementalRollup))]
    public void SetupHistogramMetricsIncrementalRollup() => SetupIncremental(isHistogram: true, HistogramMetricInstrumentName);

    private void Setup(bool isHistogram)
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-metrics-benchmark-").FullName;
        _queryRepository = CreateRepository(Path.Combine(_temporaryDirectory, "query.db"));
        var addContext = new AddContext();
        foreach (var batch in CreateLongDurationMetricBatches(DimensionCount, isHistogram))
        {
            _queryRepository.AddMetrics(addContext, batch);
        }
        if (addContext.FailureCount > 0)
        {
            throw new InvalidOperationException($"Failed to add {addContext.FailureCount} benchmark metric points.");
        }
    }

    private void SetupIncremental(bool isHistogram, string instrumentName)
    {
        Setup(isHistogram);
        var instrument = GetLongDurationInstrument(instrumentName, TimeSpan.FromMinutes(1));
        _incrementalCursors = instrument.Dimensions.Select(dimension =>
        {
            var latestValue = dimension.Values[^1];
            return new MetricDimensionCursor
            {
                Attributes = dimension.Attributes,
                StartTime = latestValue.End.Subtract(TimeSpan.FromMinutes(1))
            };
        }).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queryRepository.Dispose();
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Benchmark(Description = "TelemetryRepository: query 12h metrics display")]
    public int GetMetricsLongDuration()
    {
        var instrument = GetLongDurationInstrument(MetricInstrumentName);

        return instrument.Dimensions.Sum(dimension => dimension.Values.Count);
    }

    [Benchmark(Description = "TelemetryRepository: query 12h histogram metrics display")]
    public int GetHistogramMetricsLongDuration()
    {
        var instrument = GetLongDurationInstrument(HistogramMetricInstrumentName);

        return instrument.Dimensions.Sum(dimension =>
            dimension.Values.Count + dimension.Values.Sum(value => value.Exemplars.Count));
    }

    [Benchmark(Description = "TelemetryRepository: query 12h metrics with 1m rollup")]
    public int GetMetricsLongDurationRollup()
    {
        var instrument = GetLongDurationInstrument(MetricInstrumentName, TimeSpan.FromMinutes(1));

        return instrument.Dimensions.Sum(dimension => dimension.Values.Count);
    }

    [Benchmark(Description = "TelemetryRepository: query 12h histogram metrics with 1m rollup")]
    public int GetHistogramMetricsLongDurationRollup()
    {
        var instrument = GetLongDurationInstrument(HistogramMetricInstrumentName, TimeSpan.FromMinutes(1));

        return instrument.Dimensions.Sum(dimension =>
            dimension.Values.Count + dimension.Values.Sum(value => value.Exemplars.Count));
    }

    [Benchmark(Description = "TelemetryRepository: query incremental metrics with 1m rollup")]
    public int GetMetricsIncrementalRollup()
    {
        var instrument = GetLongDurationInstrument(MetricInstrumentName, TimeSpan.FromMinutes(1), _incrementalCursors);

        return instrument.Dimensions.Sum(dimension => dimension.Values.Count);
    }

    [Benchmark(Description = "TelemetryRepository: query incremental histogram metrics with 1m rollup")]
    public int GetHistogramMetricsIncrementalRollup()
    {
        var instrument = GetLongDurationInstrument(HistogramMetricInstrumentName, TimeSpan.FromMinutes(1), _incrementalCursors);

        return instrument.Dimensions.Sum(dimension =>
            dimension.Values.Count + dimension.Values.Sum(value => value.Exemplars.Count));
    }

    private OtlpInstrumentData GetLongDurationInstrument(
        string instrumentName,
        TimeSpan? dataPointInterval = null,
        IReadOnlyList<MetricDimensionCursor>? dimensionCursors = null)
    {
        var endTime = _queryRepository.GetInstrumentLatestEndTime(s_metricResourceKey, MetricMeterName, instrumentName)
            ?? throw new InvalidOperationException($"Unable to find the benchmark metric '{instrumentName}' end time.");

        // Match the dashboard metrics display query, which includes an extra 30 seconds for histogram calculations.
        return _queryRepository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = s_metricResourceKey,
            MeterName = MetricMeterName,
            InstrumentName = instrumentName,
            StartTime = endTime.Subtract(s_metricDisplayDuration + TimeSpan.FromSeconds(30)),
            EndTime = endTime,
            DataPointInterval = dataPointInterval,
            DimensionCursors = dimensionCursors ?? []
        }) ?? throw new InvalidOperationException($"Unable to find the benchmark metric '{instrumentName}'.");
    }

    private static SqliteTelemetryRepository CreateRepository(string databasePath)
    {
        return new SqliteTelemetryRepository(
            databasePath,
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions()),
            new PauseManager(),
            []);
    }

    private static IEnumerable<RepeatedField<ResourceMetrics>> CreateLongDurationMetricBatches(int dimensionCount, bool isHistogram)
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double[] observations = [5, 25, 75, 150];
        var bucketCounts = Enumerable.Range(0, dimensionCount).Select(_ => new ulong[observations.Length]).ToArray();
        var sums = new double[dimensionCount];
        var totalSampleCount = (int)(s_metricDataDuration / s_metricInterval);

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
            AddJob(Job.Default
                .WithWarmupCount(1)
                .WithIterationCount(1)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}