// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
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
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(Config))]
public class TelemetryRepositoryBenchmarks
{
    private const int TraceCount = 250;
    private const int SpansPerTrace = 40;

    private readonly List<TelemetryFilter> _durationFilters =
    [
        new FieldTelemetryFilter
        {
            Field = KnownTraceFields.DurationField,
            Condition = FilterCondition.GreaterThanOrEqual,
            Value = "50"
        }
    ];

    private readonly List<TelemetryFilter> _noMatchDurationFilters =
    [
        new FieldTelemetryFilter
        {
            Field = KnownTraceFields.DurationField,
            Condition = FilterCondition.GreaterThanOrEqual,
            Value = "1000"
        }
    ];

    private readonly List<TelemetryFilter> _noMatchAttributeFilters =
    [
        new FieldTelemetryFilter
        {
            Field = "missing.attribute",
            Condition = FilterCondition.Equals,
            Value = "never"
        }
    ];

    private RepeatedField<ResourceSpans> _resourceSpans = [];
    private TelemetryRepository _queryRepository = null!;

    [GlobalSetup]
    public void Setup()
    {
        _resourceSpans = CreateResourceSpans(TraceCount, SpansPerTrace);
        _queryRepository = CreateRepository();
        _queryRepository.AddTraces(new AddContext(), _resourceSpans);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queryRepository.Dispose();
    }

    [Benchmark(Description = "TelemetryRepository: add 10k spans")]
    public int AddTracesLargeBatch()
    {
        using var repository = CreateRepository();
        var context = new AddContext();
        repository.AddTraces(context, _resourceSpans);

        return context.SuccessCount;
    }

    [Benchmark(Description = "TelemetryRepository: query no filters")]
    public int GetTracesNoFilters()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            FilterText = string.Empty,
            Filters = [],
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: duration filter 10k spans")]
    public int GetTracesDurationFilter()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            FilterText = string.Empty,
            Filters = _durationFilters,
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: no-match duration 10k spans")]
    public int GetTracesNoMatchDurationFilter()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            FilterText = string.Empty,
            Filters = _noMatchDurationFilters,
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: no-match filter 10k spans")]
    public int GetTracesNoMatchAttributeFilter()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            FilterText = string.Empty,
            Filters = _noMatchAttributeFilters,
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    private static TelemetryRepository CreateRepository()
    {
        return new TelemetryRepository(
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions
            {
                TelemetryLimits = new TelemetryLimitOptions
                {
                    MaxTraceCount = TraceCount + 1
                }
            }),
            new PauseManager(),
            []);
    }

    private static RepeatedField<ResourceSpans> CreateResourceSpans(int traceCount, int spansPerTrace)
    {
        return
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = new InstrumentationScope { Name = "BenchmarkScope" },
                        Spans = { CreateSpans(traceCount, spansPerTrace) }
                    }
                }
            }
        ];
    }

    private static IEnumerable<OtlpProtoSpan> CreateSpans(int traceCount, int spansPerTrace)
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var traceIndex = 0; traceIndex < traceCount; traceIndex++)
        {
            for (var spanIndex = 0; spanIndex < spansPerTrace; spanIndex++)
            {
                var spanStartTime = startTime.AddSeconds(traceIndex).AddTicks(spanIndex);
                yield return new OtlpProtoSpan
                {
                    TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"trace-{traceIndex:0000}")),
                    SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"span-{spanIndex:0000}")),
                    ParentSpanId = spanIndex == 0
                        ? ByteString.Empty
                        : ByteString.CopyFrom(Encoding.UTF8.GetBytes($"span-{spanIndex - 1:0000}")),
                    Name = spanIndex == 0 ? "root-span" : $"span-{spanIndex}",
                    Kind = OtlpProtoSpan.Types.SpanKind.Internal,
                    StartTimeUnixNano = DateTimeToUnixNanoseconds(spanStartTime),
                    EndTimeUnixNano = DateTimeToUnixNanoseconds(spanStartTime.AddMilliseconds(spanIndex % 10 == 0 ? 100 : 5)),
                    Attributes =
                    {
                        new KeyValue
                        {
                            Key = "benchmark.index",
                            Value = new AnyValue { StringValue = spanIndex.ToString(CultureInfo.InvariantCulture) }
                        }
                    }
                };
            }
        }
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
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(16)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}
