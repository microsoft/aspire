// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
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
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
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
    private string _temporaryDirectory = null!;
    private DashboardSqliteDatabase _queryDatabase = null!;
    private SqliteTelemetryRepository _queryRepository = null!;
    private DashboardSqliteDatabase? _addDatabase;
    private SqliteTelemetryRepository? _addRepository;
    private AddContext _addContext = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _resourceSpans = CreateResourceSpans(TraceCount, SpansPerTrace);
        _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-telemetry-benchmark-").FullName;
        _queryDatabase = new DashboardSqliteDatabase(Path.Combine(_temporaryDirectory, "query.db"));
        await _queryDatabase.InitializeSchemaAsync(CancellationToken.None);
        _queryRepository = CreateRepository(_queryDatabase);
        await _queryRepository.AddTracesAsync(new AddContext(), _resourceSpans);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        DisposeAddTracesDatabase();
        _queryRepository.Dispose();
        _queryDatabase.ClearPool();
        _queryDatabase.Dispose();
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [IterationSetup(Target = nameof(AddTracesLargeBatch))]
    public async Task SetupAddTracesIteration()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "add", "telemetry.db");
        _addDatabase = new DashboardSqliteDatabase(databasePath);
        await _addDatabase.InitializeSchemaAsync(CancellationToken.None);
        _addRepository = CreateRepository(_addDatabase);
        _addContext = new AddContext();
    }

    [IterationCleanup(Target = nameof(AddTracesLargeBatch))]
    public void CleanupAddTracesIteration()
    {
        DisposeAddTracesDatabase();
        Directory.Delete(Path.Combine(_temporaryDirectory, "add"), recursive: true);
        if (_addContext.SuccessCount != TraceCount * SpansPerTrace)
        {
            throw new InvalidOperationException($"Expected to ingest {TraceCount * SpansPerTrace} spans, but ingested {_addContext.SuccessCount}.");
        }
    }

    // Run exactly one batch per iteration so that every invocation starts with an empty database.
    [InvocationCount(1)]
    [Benchmark(Description = "TelemetryRepository: add 10k spans")]
    public async Task<int> AddTracesLargeBatch()
    {
        await _addRepository!.AddTracesAsync(_addContext, _resourceSpans);
        return _addContext.SuccessCount;
    }

    [Benchmark(Description = "TelemetryRepository: query no filters")]
    public async Task<int> GetTracesNoFilters()
    {
        var result = await _queryRepository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = [],
            StartIndex = 0,
            Count = 100
        }, CancellationToken.None);

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: duration filter 10k spans")]
    public async Task<int> GetTracesDurationFilter()
    {
        var result = await _queryRepository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = _durationFilters,
            StartIndex = 0,
            Count = 100
        }, CancellationToken.None);

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: no-match duration 10k spans")]
    public async Task<int> GetTracesNoMatchDurationFilter()
    {
        var result = await _queryRepository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = _noMatchDurationFilters,
            StartIndex = 0,
            Count = 100
        }, CancellationToken.None);

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: no-match filter 10k spans")]
    public async Task<int> GetTracesNoMatchAttributeFilter()
    {
        var result = await _queryRepository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = _noMatchAttributeFilters,
            StartIndex = 0,
            Count = 100
        }, CancellationToken.None);

        return result.PagedResult.Items.Count;
    }

    private void DisposeAddTracesDatabase()
    {
        _addRepository?.Dispose();
        _addRepository = null;
        if (_addDatabase is not null)
        {
            _addDatabase.ClearPool();
            _addDatabase.Dispose();
            _addDatabase = null;
        }
    }

    private static SqliteTelemetryRepository CreateRepository(DashboardSqliteDatabase database)
    {
        return new SqliteTelemetryRepository(
            database,
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions
            {
                TelemetryLimits = new TelemetryLimitOptions
                {
                    MaxTraceCount = TraceCount + 1
                }
            }),
            new PauseManager(),
            TimeProvider.System,
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
