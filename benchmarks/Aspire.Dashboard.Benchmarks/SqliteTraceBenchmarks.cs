// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
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
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(Config))]
public class SqliteTraceBenchmarks
{
    private const string TraceFileEnvironmentVariable = "ASPIRE_DASHBOARD_TRACE_BENCHMARK_FILE";
    private const int GeneratedSpanCount = 10_000;

    private string _temporaryDirectory = null!;
    private SqliteTelemetryRepository _repository = null!;
    private RepeatedField<ResourceSpans> _appendResourceSpans = null!;
    private long _appendIndex;

    [GlobalSetup]
    public void Setup()
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-trace-benchmark-").FullName;
        _repository = CreateRepository(Path.Combine(_temporaryDirectory, "dashboard.db"));

        var resourceSpans = LoadResourceSpans();
        var context = new AddContext();
        _repository.AddTraces(context, resourceSpans);
        if (context.FailureCount != 0)
        {
            throw new InvalidOperationException($"Failed to ingest {context.FailureCount} benchmark spans.");
        }

        _appendResourceSpans = CreateAppendResourceSpans(resourceSpans);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _repository.Dispose();
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Benchmark(Description = "SQLite: summarize large trace")]
    public int GetTraceSummaries()
    {
        var response = _repository.GetTraceSummaries(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 100,
            Filters = []
        });

        return response.PagedResult.Items.Count;
    }

    [Benchmark(Description = "SQLite: append one span to large trace")]
    public int AppendSpan()
    {
        var appendSpan = _appendResourceSpans[0].ScopeSpans[0].Spans[0];
        appendSpan.SpanId = ByteString.CopyFrom(BitConverter.GetBytes(long.MaxValue - Interlocked.Increment(ref _appendIndex)));
        var context = new AddContext();
        _repository.AddTraces(context, _appendResourceSpans);
        return context.SuccessCount;
    }

    private static RepeatedField<ResourceSpans> LoadResourceSpans()
    {
        var traceFile = Environment.GetEnvironmentVariable(TraceFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(traceFile))
        {
            return CreateGeneratedResourceSpans();
        }

        var request = JsonParser.Default.Parse<ExportTraceServiceRequest>(File.ReadAllText(traceFile));
        return request.ResourceSpans;
    }

    private static RepeatedField<ResourceSpans> CreateGeneratedResourceSpans()
    {
        var resourceSpans = new ResourceSpans
        {
            Resource = CreateResource("benchmark-app"),
            ScopeSpans =
            {
                new ScopeSpans
                {
                    Scope = new InstrumentationScope { Name = "BenchmarkScope" }
                }
            }
        };
        var scopeSpans = resourceSpans.ScopeSpans[0];
        var traceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("benchmark-trace"));
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var spanIndex = 0; spanIndex < GeneratedSpanCount; spanIndex++)
        {
            var spanStartTime = startTime.AddTicks(spanIndex);
            scopeSpans.Spans.Add(new OtlpProtoSpan
            {
                TraceId = traceId,
                SpanId = CreateSpanId(spanIndex),
                ParentSpanId = spanIndex == 0 ? ByteString.Empty : CreateSpanId(spanIndex - 1),
                Name = spanIndex == 0 ? "root-span" : $"span-{spanIndex}",
                Kind = OtlpProtoSpan.Types.SpanKind.Internal,
                StartTimeUnixNano = DateTimeToUnixNanoseconds(spanStartTime),
                EndTimeUnixNano = DateTimeToUnixNanoseconds(spanStartTime.AddMilliseconds(5))
            });
        }

        return [resourceSpans];
    }

    private static RepeatedField<ResourceSpans> CreateAppendResourceSpans(RepeatedField<ResourceSpans> resourceSpans)
    {
        var firstSpan = resourceSpans.SelectMany(resource => resource.ScopeSpans).SelectMany(scope => scope.Spans).First();
        return
        [
            new ResourceSpans
            {
                Resource = CreateResource("append-app"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = new InstrumentationScope { Name = "AppendScope" },
                        Spans =
                        {
                            new OtlpProtoSpan
                            {
                                TraceId = firstSpan.TraceId,
                                SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("append-span-0001")),
                                ParentSpanId = firstSpan.SpanId,
                                Name = "appended-span",
                                Kind = OtlpProtoSpan.Types.SpanKind.Internal,
                                StartTimeUnixNano = firstSpan.EndTimeUnixNano + 100,
                                EndTimeUnixNano = firstSpan.EndTimeUnixNano + 200
                            }
                        }
                    }
                }
            }
        ];
    }

    private static SqliteTelemetryRepository CreateRepository(string databasePath)
    {
        return new SqliteTelemetryRepository(
            databasePath,
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions
            {
                TelemetryLimits = new TelemetryLimitOptions { MaxTraceCount = 1_000 }
            }),
            new PauseManager(),
            []);
    }

    private static Resource CreateResource(string name)
    {
        return new Resource
        {
            Attributes =
            {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } }
            }
        };
    }

    private static ByteString CreateSpanId(int spanIndex) =>
        ByteString.CopyFrom(Encoding.UTF8.GetBytes($"span-{spanIndex:0000}"));

    private static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var timeSinceEpoch = dateTime.ToUniversalTime() - DateTime.UnixEpoch;
        return (ulong)timeSinceEpoch.Ticks * 100;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithWarmupCount(1)
                .WithIterationCount(3)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}