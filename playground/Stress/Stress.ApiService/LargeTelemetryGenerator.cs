// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Stress.ApiService;

public sealed class LargeTelemetryGenerator(ILogger<LargeTelemetryGenerator> logger, IConfiguration configuration)
{
    public const int TraceCount = 50_000;
    public const int SpansPerTrace = 6;
    public const int LargeTraceSpanCount = 50_000;
    public const int StructuredLogCount = 100_000;
    public const int ConsoleLogCount = 100_000;
    public const int MetricDurationSeconds = 24 * 60 * 60;
    public const int MetricDimensionCount = 5;

    private const int TraceBatchSize = 1_000;
    private const int LargeTraceBatchSize = 1_000;
    private const int LogBatchSize = 1_000;
    private const int MetricSecondsPerBatch = 100;
    private static readonly double[] s_histogramValues = [5, 25, 75, 150];
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public async Task<bool> TryGenerateAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
                ?? throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT is required.");
            using var channel = GrpcChannel.ForAddress(endpoint);
            var metadata = CreateMetadata(configuration["OTEL_EXPORTER_OTLP_HEADERS"]);
            var traceClient = new TraceService.TraceServiceClient(channel);
            var logsClient = new LogsService.LogsServiceClient(channel);
            var metricsClient = new MetricsService.MetricsServiceClient(channel);

            logger.LogInformation("Generating {TraceCount} traces with {SpansPerTrace} spans each.", TraceCount, SpansPerTrace);
            await ExportTracesAsync(traceClient, metadata, cancellationToken);

            logger.LogInformation("Generating one trace with {SpanCount} spans.", LargeTraceSpanCount);
            await ExportLargeTraceAsync(traceClient, metadata, cancellationToken);

            logger.LogInformation("Generating {LogCount} structured logs.", StructuredLogCount);
            await ExportStructuredLogsAsync(logsClient, metadata, cancellationToken);

            logger.LogInformation("Writing {LogCount} console logs through ILogger.", ConsoleLogCount);
            for (var logIndex = 0; logIndex < ConsoleLogCount; logIndex++)
            {
                logger.LogInformation("Large console log {LogIndex}.", logIndex + 1);
                if ((logIndex + 1) % LogBatchSize == 0)
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            logger.LogInformation(
                "Generating {DurationSeconds} seconds of counter and histogram data across {DimensionCount} dimensions.",
                MetricDurationSeconds,
                MetricDimensionCount);
            await ExportMetricsAsync(metricsClient, metadata, cancellationToken);

            logger.LogInformation("Large telemetry generation completed.");
            return true;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static async Task ExportTracesAsync(
        TraceService.TraceServiceClient client,
        Metadata metadata,
        CancellationToken cancellationToken)
    {
        var finalTraceTime = DateTime.UtcNow;
        for (var firstTraceIndex = 0; firstTraceIndex < TraceCount; firstTraceIndex += TraceBatchSize)
        {
            var scopeSpans = CreateScopeSpans("LargeTelemetry.ManyTraces");
            var traceCount = Math.Min(TraceBatchSize, TraceCount - firstTraceIndex);
            for (var traceOffset = 0; traceOffset < traceCount; traceOffset++)
            {
                var traceIndex = firstTraceIndex + traceOffset;
                var traceId = CreateTraceId(discriminator: 1, traceIndex + 1);
                var traceStart = finalTraceTime.AddMilliseconds(traceIndex - TraceCount);
                for (var spanIndex = 0; spanIndex < SpansPerTrace; spanIndex++)
                {
                    var isRoot = spanIndex == 0;
                    scopeSpans.Spans.Add(CreateSpan(
                        traceId,
                        CreateSpanId(spanIndex + 1),
                        isRoot ? ByteString.Empty : CreateSpanId(1),
                        $"GET /stress/page/{spanIndex + 1}",
                        traceStart.AddMilliseconds(spanIndex),
                        isRoot ? traceStart.AddMilliseconds(SpansPerTrace) : traceStart.AddMilliseconds(spanIndex + 1)));
                }
            }

            await ExportTraceBatchAsync(client, metadata, scopeSpans, cancellationToken);
        }
    }

    private static async Task ExportLargeTraceAsync(
        TraceService.TraceServiceClient client,
        Metadata metadata,
        CancellationToken cancellationToken)
    {
        var traceId = CreateTraceId(discriminator: 2, value: 1);
        var traceStart = DateTime.UtcNow.AddMinutes(-1);
        for (var firstSpanIndex = 0; firstSpanIndex < LargeTraceSpanCount; firstSpanIndex += LargeTraceBatchSize)
        {
            var scopeSpans = CreateScopeSpans("LargeTelemetry.LargeTrace");
            var spanCount = Math.Min(LargeTraceBatchSize, LargeTraceSpanCount - firstSpanIndex);
            for (var spanOffset = 0; spanOffset < spanCount; spanOffset++)
            {
                var spanIndex = firstSpanIndex + spanOffset;
                var isRoot = spanIndex == 0;
                scopeSpans.Spans.Add(CreateSpan(
                    traceId,
                    CreateSpanId(spanIndex + 1),
                    isRoot ? ByteString.Empty : CreateSpanId(1),
                    $"large-trace-span-{spanIndex + 1}",
                    traceStart.AddTicks(spanIndex * 10L),
                    isRoot ? traceStart.AddTicks(LargeTraceSpanCount * 10L) : traceStart.AddTicks((spanIndex + 1L) * 10L)));
            }

            await ExportTraceBatchAsync(client, metadata, scopeSpans, cancellationToken);
        }
    }

    private static async Task ExportTraceBatchAsync(
        TraceService.TraceServiceClient client,
        Metadata metadata,
        ScopeSpans scopeSpans,
        CancellationToken cancellationToken)
    {
        // Export requests use the OTLP shape ResourceSpans -> ScopeSpans -> Span. Keeping each request
        // below 6,000 spans avoids large gRPC messages while allowing one trace to cross request boundaries.
        var request = new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    Resource = CreateResource("large-telemetry-traces"),
                    ScopeSpans = { scopeSpans }
                }
            }
        };
        var response = await client.ExportAsync(request, headers: metadata, cancellationToken: cancellationToken);
        if (response.PartialSuccess is { RejectedSpans: > 0 } partialSuccess)
        {
            throw new InvalidOperationException($"Dashboard rejected {partialSuccess.RejectedSpans} spans: {partialSuccess.ErrorMessage}");
        }
    }

    private static async Task ExportStructuredLogsAsync(
        LogsService.LogsServiceClient client,
        Metadata metadata,
        CancellationToken cancellationToken)
    {
        var finalLogTime = DateTime.UtcNow;
        for (var firstLogIndex = 0; firstLogIndex < StructuredLogCount; firstLogIndex += LogBatchSize)
        {
            var scopeLogs = new ScopeLogs
            {
                Scope = new InstrumentationScope { Name = "LargeTelemetry.StructuredLogs" }
            };
            var logCount = Math.Min(LogBatchSize, StructuredLogCount - firstLogIndex);
            for (var logOffset = 0; logOffset < logCount; logOffset++)
            {
                var logIndex = firstLogIndex + logOffset;
                var timestamp = DateTimeToUnixNanoseconds(finalLogTime.AddMilliseconds(logIndex - StructuredLogCount));
                scopeLogs.LogRecords.Add(new LogRecord
                {
                    TimeUnixNano = timestamp,
                    ObservedTimeUnixNano = timestamp,
                    SeverityNumber = SeverityNumber.Info,
                    SeverityText = "Information",
                    Body = new AnyValue { StringValue = $"Large structured log {logIndex + 1}." },
                    Attributes =
                    {
                        new KeyValue { Key = "{OriginalFormat}", Value = new AnyValue { StringValue = "Large structured log {LogIndex}." } },
                        new KeyValue { Key = "LogIndex", Value = new AnyValue { IntValue = logIndex + 1 } }
                    }
                });
            }

            // Log batches use the OTLP shape ResourceLogs -> ScopeLogs -> LogRecord.
            var request = new ExportLogsServiceRequest
            {
                ResourceLogs =
                {
                    new ResourceLogs
                    {
                        Resource = CreateResource("large-telemetry-structured-logs"),
                        ScopeLogs = { scopeLogs }
                    }
                }
            };
            var response = await client.ExportAsync(request, headers: metadata, cancellationToken: cancellationToken);
            if (response.PartialSuccess is { RejectedLogRecords: > 0 } partialSuccess)
            {
                throw new InvalidOperationException($"Dashboard rejected {partialSuccess.RejectedLogRecords} logs: {partialSuccess.ErrorMessage}");
            }
        }
    }

    private static async Task ExportMetricsAsync(
        MetricsService.MetricsServiceClient client,
        Metadata metadata,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow.AddDays(-1);
        for (var firstSecond = 0; firstSecond < MetricDurationSeconds; firstSecond += MetricSecondsPerBatch)
        {
            var counter = new Metric
            {
                Name = "large.telemetry.counter",
                Description = "One day of one-second counter data.",
                Unit = "requests",
                Sum = new Sum
                {
                    AggregationTemporality = AggregationTemporality.Cumulative,
                    IsMonotonic = true
                }
            };
            var histogram = new Metric
            {
                Name = "large.telemetry.histogram",
                Description = "One day of one-second histogram data with exemplars.",
                Unit = "ms",
                Histogram = new Histogram
                {
                    AggregationTemporality = AggregationTemporality.Cumulative
                }
            };

            var secondCount = Math.Min(MetricSecondsPerBatch, MetricDurationSeconds - firstSecond);
            for (var secondOffset = 0; secondOffset < secondCount; secondOffset++)
            {
                var secondIndex = firstSecond + secondOffset;
                var pointTime = startTime.AddSeconds(secondIndex + 1L);
                var timestamp = DateTimeToUnixNanoseconds(pointTime);
                for (var dimensionIndex = 0; dimensionIndex < MetricDimensionCount; dimensionIndex++)
                {
                    var dimension = new KeyValue
                    {
                        Key = "stress.dimension",
                        Value = new AnyValue { StringValue = dimensionIndex.ToString(CultureInfo.InvariantCulture) }
                    };
                    counter.Sum.DataPoints.Add(new NumberDataPoint
                    {
                        StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
                        TimeUnixNano = timestamp,
                        AsInt = (long)(secondIndex + 1) * (dimensionIndex + 1),
                        Attributes = { dimension }
                    });

                    var observationIndex = secondIndex % s_histogramValues.Length;
                    var histogramPoint = new HistogramDataPoint
                    {
                        StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
                        TimeUnixNano = timestamp,
                        Count = (ulong)secondIndex + 1,
                        Sum = CalculateHistogramSum(secondIndex + 1),
                        ExplicitBounds = { 10, 50, 100 },
                        Attributes = { dimension },
                        Exemplars =
                        {
                            new Exemplar
                            {
                                TimeUnixNano = timestamp,
                                AsDouble = s_histogramValues[observationIndex],
                                TraceId = CreateTraceId(discriminator: 1, (secondIndex % TraceCount) + 1),
                                SpanId = CreateSpanId((dimensionIndex % SpansPerTrace) + 1)
                            }
                        }
                    };
                    histogramPoint.BucketCounts.Add(CalculateBucketCount(secondIndex + 1, 0));
                    histogramPoint.BucketCounts.Add(CalculateBucketCount(secondIndex + 1, 1));
                    histogramPoint.BucketCounts.Add(CalculateBucketCount(secondIndex + 1, 2));
                    histogramPoint.BucketCounts.Add(CalculateBucketCount(secondIndex + 1, 3));
                    histogram.Histogram.DataPoints.Add(histogramPoint);
                }
            }

            // Metric batches use ResourceMetrics -> ScopeMetrics -> Metric and contain 100 seconds at a
            // time. At five dimensions and two instruments this caps each request at 1,000 data points.
            var request = new ExportMetricsServiceRequest
            {
                ResourceMetrics =
                {
                    new ResourceMetrics
                    {
                        Resource = CreateResource("large-telemetry-metrics"),
                        ScopeMetrics =
                        {
                            new ScopeMetrics
                            {
                                Scope = new InstrumentationScope { Name = "LargeTelemetry.Metrics" },
                                Metrics = { counter, histogram }
                            }
                        }
                    }
                }
            };
            var response = await client.ExportAsync(request, headers: metadata, cancellationToken: cancellationToken);
            if (response.PartialSuccess is { RejectedDataPoints: > 0 } partialSuccess)
            {
                throw new InvalidOperationException($"Dashboard rejected {partialSuccess.RejectedDataPoints} metric points: {partialSuccess.ErrorMessage}");
            }
        }
    }

    private static ScopeSpans CreateScopeSpans(string name) => new()
    {
        Scope = new InstrumentationScope { Name = name }
    };

    private static OtlpSpan CreateSpan(
        ByteString traceId,
        ByteString spanId,
        ByteString parentSpanId,
        string name,
        DateTime startTime,
        DateTime endTime) => new()
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Name = name,
            Kind = OtlpSpan.Types.SpanKind.Server,
            StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
            EndTimeUnixNano = DateTimeToUnixNanoseconds(endTime),
            Status = new OpenTelemetry.Proto.Trace.V1.Status
            {
                Code = OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Ok
            }
        };

    private static Resource CreateResource(string serviceName) => new()
    {
        Attributes =
        {
            new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = serviceName } },
            new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "large-telemetry-1" } }
        }
    };

    private static Metadata CreateMetadata(string? headers)
    {
        var metadata = new Metadata();
        if (string.IsNullOrWhiteSpace(headers))
        {
            return metadata;
        }

        // OTEL_EXPORTER_OTLP_HEADERS uses comma-separated key=value pairs. Values may contain '=' and
        // may be URL encoded, so split each pair only at its first '=' delimiter.
        foreach (var pair in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            metadata.Add(
                pair[..separatorIndex].Trim().ToLowerInvariant(),
                Uri.UnescapeDataString(pair[(separatorIndex + 1)..].Trim()));
        }
        return metadata;
    }

    private static ByteString CreateTraceId(byte discriminator, int value)
    {
        var id = new byte[16];
        id[0] = discriminator;
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), value);
        return ByteString.CopyFrom(id);
    }

    private static ByteString CreateSpanId(int value)
    {
        var id = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(4), value);
        return ByteString.CopyFrom(id);
    }

    private static ulong CalculateBucketCount(int sampleCount, int observationIndex)
    {
        var completeCycles = sampleCount / s_histogramValues.Length;
        var remainder = sampleCount % s_histogramValues.Length;
        return (ulong)(completeCycles + (observationIndex < remainder ? 1 : 0));
    }

    private static double CalculateHistogramSum(int sampleCount)
    {
        var completeCycles = sampleCount / s_histogramValues.Length;
        var remainder = sampleCount % s_histogramValues.Length;
        var cycleSum = s_histogramValues.Sum();
        return completeCycles * cycleSum + s_histogramValues.Take(remainder).Sum();
    }

    private static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var timeSinceEpoch = dateTime.ToUniversalTime() - DateTime.UnixEpoch;
        return (ulong)timeSinceEpoch.Ticks * 100;
    }
}