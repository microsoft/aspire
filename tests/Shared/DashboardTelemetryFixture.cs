// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Channels;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Aspire.Dashboard.Tests;

public sealed class DashboardTelemetryFixture : IDisposable
{
    private readonly ActivityListener _listener;

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
            DashboardTelemetryManager.ConfigureEventLogging(builder);
            builder.AddOpenTelemetry(logging => logging.AddProcessor(new EventLogProcessor(LogChannel.Writer)));
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
            Configuration, LoggerFactory, ActivitySourceName, DiagnosticsActivitySourceName);
    }

    public void Dispose()
    {
        Telemetry.Dispose();
        _listener.Dispose();
        LoggerFactory.Dispose();
        ActivityChannel.Writer.TryComplete();
        LogChannel.Writer.TryComplete();
    }

    private sealed class EventLogProcessor(ChannelWriter<TestDashboardTelemetryLog> writer) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            // LogRecord instances are pooled, so copy their data before returning to the SDK.
            writer.TryWrite(new TestDashboardTelemetryLog(
                data.FormattedMessage, data.LogLevel, data.EventId, data.CategoryName,
                data.TraceId, data.SpanId, data.Attributes?.ToArray() ?? []));
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
    IReadOnlyList<KeyValuePair<string, object?>> Attributes);
