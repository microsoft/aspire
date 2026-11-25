// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(Config))]
public class SqliteTimestampFilterBenchmarks
{
    private const int ResourceCount = 100;
    private const int TraceCount = 20_000;
    private const int SpansPerTrace = 24;
    private const int LogsPerTrace = 10;
    private const int TracesPerSecond = 10;
    private const int WindowSeconds = 30;
    private const int PageSize = 100;
    private static readonly DateTime s_start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private DirectoryInfo _temporaryDirectory = null!;
    private DashboardSqliteDatabase _database = null!;
    private SqliteTelemetryRepository _repository = null!;
    private GetTracesRequest _allResourcesRequest = null!;
    private GetTracesRequest _selectedResourceRequest = null!;
    private GetLogsContext _logsRequest = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-timestamp-benchmark-");
        _database = new DashboardSqliteDatabase(Path.Combine(_temporaryDirectory.FullName, "dashboard.db"));
        await _database.InitializeSchemaAsync(CancellationToken.None);
        _repository = new SqliteTelemetryRepository(
            _database,
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions
            {
                TelemetryLimits = new TelemetryLimitOptions { MaxTraceCount = TraceCount + 1, MaxLogCount = TraceCount * LogsPerTrace + 1 }
            }),
            new PauseManager(),
            TimeProvider.System,
            []);

        var resources = Enumerable.Range(0, ResourceCount)
            .Select(index => new ResourceKey($"service-{index.ToString(CultureInfo.InvariantCulture)}", null))
            .ToArray();
        await SeedAsync(resources);

        var windowStart = s_start.AddMinutes(15);
        var windowEnd = windowStart.AddSeconds(WindowSeconds);
        _allResourcesRequest = TraceRequest(resources, windowStart, windowEnd);
        // A wide window makes time alone unselective: the chosen resource owns only 1% of the traces.
        _selectedResourceRequest = TraceRequest([resources[0]], s_start, s_start.AddSeconds(TraceCount / TracesPerSecond));
        _logsRequest = new GetLogsContext
        {
            ResourceKeys = resources,
            StartIndex = 0,
            Count = PageSize,
            Filters = TimestampRange(KnownStructuredLogFields.TimestampField, windowStart, windowEnd)
        };

        VerifyCount(await TraceSummariesForActivityBucket(), WindowSeconds * TracesPerSecond, nameof(TraceSummariesForActivityBucket));
        VerifyCount(await TraceSummariesForSelectedResource(), TraceCount / ResourceCount, nameof(TraceSummariesForSelectedResource));
        VerifyCount(await LogSummariesForTimeWindow(), WindowSeconds * TracesPerSecond * LogsPerTrace, nameof(LogSummariesForTimeWindow));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _repository.Dispose();
        _database.ClearPool();
        _database.Dispose();
        _temporaryDirectory.Delete(recursive: true);
    }

    [Benchmark]
    public async Task<int> TraceSummariesForActivityBucket() =>
        (await _repository.GetTraceSummariesAsync(_allResourcesRequest, CancellationToken.None)).PagedResult.TotalItemCount;

    [Benchmark]
    public async Task<int> TraceSummariesForSelectedResource() =>
        (await _repository.GetTraceSummariesAsync(_selectedResourceRequest, CancellationToken.None)).PagedResult.TotalItemCount;

    [Benchmark]
    public async Task<int> LogSummariesForTimeWindow() =>
        (await _repository.GetLogSummariesAsync(_logsRequest, CancellationToken.None)).TotalItemCount;

    private async Task SeedAsync(ResourceKey[] resources)
    {
        var scope = new InstrumentationScope { Name = "benchmark" };
        var spanIds = Enumerable.Range(0, SpansPerTrace)
            .Select(index => ByteString.CopyFromUtf8(index.ToString("x8", CultureInfo.InvariantCulture)))
            .ToArray();
        for (var resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            var resource = new Resource { Attributes = { Attribute("service.name", resources[resourceIndex].Name) } };
            var spans = new ScopeSpans { Scope = scope };
            var logs = new ScopeLogs { Scope = scope };
            for (var traceIndex = resourceIndex; traceIndex < TraceCount; traceIndex += resources.Length)
            {
                var traceId = ByteString.CopyFromUtf8(traceIndex.ToString("x16", CultureInfo.InvariantCulture));
                var start = ToUnixNanoseconds(s_start.AddMilliseconds(traceIndex * (1000 / TracesPerSecond)));
                for (var spanIndex = 0; spanIndex < spanIds.Length; spanIndex++)
                {
                    var timestamp = start + (ulong)spanIndex * 1_000_000;
                    spans.Spans.Add(new Span
                    {
                        TraceId = traceId,
                        SpanId = spanIds[spanIndex],
                        ParentSpanId = spanIndex == 0 ? ByteString.Empty : spanIds[0],
                        Name = spanIndex == 0 ? "request" : "operation",
                        Kind = spanIndex == 0 ? Span.Types.SpanKind.Server : Span.Types.SpanKind.Internal,
                        StartTimeUnixNano = timestamp,
                        EndTimeUnixNano = timestamp + 1_000_000
                    });
                }

                for (var logIndex = 0; logIndex < LogsPerTrace; logIndex++)
                {
                    var log = new LogRecord
                    {
                        TraceId = traceId,
                        SpanId = spanIds[0],
                        TimeUnixNano = start + (ulong)logIndex * 1_000_000,
                        Body = new AnyValue { StringValue = "benchmark log" },
                        Attributes = { Attribute("benchmark.attribute", "value") }
                    };
                    if (logIndex == 0)
                    {
                        log.Attributes.Add(Attribute("exception.type", "InvalidOperationException"));
                        log.Attributes.Add(Attribute("exception.message", "Example exception"));
                    }
                    else if (logIndex == 1)
                    {
                        log.Attributes.Add(Attribute("gen_ai.provider.name", "benchmark-provider"));
                    }
                    logs.LogRecords.Add(log);
                }
            }

            var traceContext = new AddContext();
            await _repository.AddTracesAsync(traceContext, [new ResourceSpans { Resource = resource, ScopeSpans = { spans } }]);
            VerifyCount(traceContext.SuccessCount, spans.Spans.Count, "ingested spans");
            var logContext = new AddContext();
            await _repository.AddLogsAsync(logContext, [new ResourceLogs { Resource = resource, ScopeLogs = { logs } }]);
            VerifyCount(logContext.SuccessCount, logs.LogRecords.Count, "ingested logs");
        }
    }

    private static GetTracesRequest TraceRequest(IReadOnlyList<ResourceKey> resources, DateTime start, DateTime end) => new()
    {
        ResourceKeys = resources,
        StartIndex = 0,
        Count = PageSize,
        Filters = TimestampRange(KnownTraceFields.TimestampField, start, end)
    };

    private static List<TelemetryFilter> TimestampRange(string field, DateTime start, DateTime end) =>
    [
        new FieldTelemetryFilter { Field = field, Condition = FilterCondition.GreaterThanOrEqual, Value = start.ToString("O", CultureInfo.InvariantCulture) },
        new FieldTelemetryFilter { Field = field, Condition = FilterCondition.LessThan, Value = end.ToString("O", CultureInfo.InvariantCulture) }
    ];

    private static KeyValue Attribute(string key, string value) => new() { Key = key, Value = new AnyValue { StringValue = value } };

    private static ulong ToUnixNanoseconds(DateTime timestamp) => (ulong)(timestamp - DateTime.UnixEpoch).Ticks * 100;

    private static void VerifyCount(int actual, int expected, string scenario)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Unexpected count for {scenario}: expected {expected}, got {actual}.");
        }
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .DontEnforcePowerPlan()
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(20)
                .WithUnrollFactor(1));
        }
    }
}
