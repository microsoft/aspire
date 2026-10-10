// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Channels;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Dashboard.Tests;

public sealed class DashboardTelemetryFixture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<AzureMonitorTelemetryProvider> _providers = [];

    public string ActivitySourceName { get; } = $"Test.Dashboard.{Guid.NewGuid():N}";
    public string DiagnosticsActivitySourceName => ActivitySourceName + ".Diagnostics";
    public Channel<Activity> ActivityChannel { get; } = Channel.CreateUnbounded<Activity>();
    public Channel<TestDashboardTelemetryLog> LogChannel { get; } = Channel.CreateUnbounded<TestDashboardTelemetryLog>();
    public ILoggerFactory LoggerFactory { get; }
    public ILogger EventLogger { get; }
    public TestSink LocalLogSink { get; } = new();
    public DashboardTelemetryConfiguration Configuration { get; }
    public DashboardTelemetryService Telemetry { get; }

    public DashboardTelemetryFixture(bool reportedTelemetryEnabled = true, ActivitySamplingResult sampleResult = ActivitySamplingResult.AllDataAndRecorded, ILogger<DashboardTelemetryService>? logger = null)
    {
        Configuration = new() { ReportedTelemetryEnabled = reportedTelemetryEnabled };
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(LocalLogSink));
        });
        EventLogger = LoggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampleResult,
            ActivityStopped = activity => ActivityChannel.Writer.TryWrite(activity)
        };
        ActivitySource.AddActivityListener(_listener);
        Telemetry = new DashboardTelemetryService(logger ?? LoggerFactory.CreateLogger<DashboardTelemetryService>(),
            Configuration, ActivitySourceName, DiagnosticsActivitySourceName);
        ConfigureLogging(Telemetry);
    }

    public void ConfigureLogging(AspireTelemetryBase telemetry)
    {
        if (!Configuration.ReportedTelemetryEnabled)
        {
            return;
        }
        var logProvider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), ResourceBuilder.CreateEmpty(),
            DashboardTelemetryService.EventLogCategoryName,
            () => Sdk.CreateTracerProviderBuilder().Build(),
            provider => provider.AddProcessor(new EventLogProcessor(LogChannel.Writer)));
        try
        {
            telemetry.SetEventLogger(logProvider.EventLogger);
            _providers.Add(logProvider);
        }
        catch
        {
            logProvider.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Telemetry.Dispose();
        foreach (var logProvider in _providers)
        {
            logProvider.Dispose();
        }
        _listener.Dispose();
        LoggerFactory.Dispose();
        ActivityChannel.Writer.TryComplete();
        LogChannel.Writer.TryComplete();
    }

    private sealed class EventLogProcessor(ChannelWriter<TestDashboardTelemetryLog> writer) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            writer.TryWrite(TestDashboardTelemetryLog.Create(data));
        }
    }
}

public sealed record TestDashboardTelemetryLog(
    string? Message,
    LogLevel Level,
    EventId EventId,
    string? CategoryName,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes)
{
    internal static TestDashboardTelemetryLog Create(LogRecord data)
    {
        // LogRecord instances are pooled, so copy their data before returning to the SDK.
        return new(data.FormattedMessage, data.LogLevel, data.EventId, data.CategoryName,
            data.TraceId, data.SpanId, data.Attributes?.ToArray() ?? []);
    }
}

internal sealed class TestDashboardTelemetryLogExporter : BaseExporter<LogRecord>
{
    public Channel<TestDashboardTelemetryLog> LogChannel { get; } = Channel.CreateUnbounded<TestDashboardTelemetryLog>();
    public bool IsDisposed { get; private set; }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        foreach (var record in batch)
        {
            LogChannel.Writer.TryWrite(TestDashboardTelemetryLog.Create(record));
        }

        return ExportResult.Success;
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
