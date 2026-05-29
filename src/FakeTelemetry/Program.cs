// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Needed for AddConsoleExporter
#pragma warning disable CA2007

// Enable OpenTelemetry self-diagnostics
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

// Enable OTEL SDK internal logging to see export success/failure
var selfDiagListener = new ActivityListener
{
    ShouldListenTo = source => source.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal),
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(selfDiagListener);

// Subscribe to OpenTelemetry SDK events to see export results
using var otelEventListener = new OtelEventListener();

var listener = new ActivityListener
{
    ShouldListenTo = _ => true,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(listener);

var serviceName = "FakeService";
var serviceVersion = "1.0.0";

var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(serviceName: serviceName, serviceVersion: serviceVersion);

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(serviceName)
    .SetResourceBuilder(resourceBuilder)
    .AddOtlpExporter(o =>
    {
        o.Endpoint = new Uri("https://otel-e360.azurewebsites.net/v1/traces");
        o.Protocol = OtlpExportProtocol.HttpProtobuf;
    })
    .AddConsoleExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter(serviceName)
    .SetResourceBuilder(resourceBuilder)
    .AddOtlpExporter(o =>
    {
        o.Endpoint = new Uri("https://otel-e360.azurewebsites.net/v1/metrics");
        o.Protocol = OtlpExportProtocol.HttpProtobuf;
    })
    .AddConsoleExporter()
    .Build();

using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddOpenTelemetry(o =>
    {
        o.SetResourceBuilder(resourceBuilder);
        o.AddOtlpExporter(e =>
        {
            e.Endpoint = new Uri("https://otel-e360.azurewebsites.net/v1/logs");
            e.Protocol = OtlpExportProtocol.HttpProtobuf;
        });
        o.AddConsoleExporter();
    });
});
var appLogger = loggerFactory.CreateLogger("FakeService");

var activitySource = new ActivitySource(serviceName);
var meter = new Meter(serviceName, serviceVersion);
var requestCounter = meter.CreateCounter<long>("requests.count", description: "Number of requests");
var requestDuration = meter.CreateHistogram<double>("requests.duration", unit: "ms", description: "Request duration");

Console.WriteLine("Sending fake telemetry to http://localhost:4318...");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

// Quick connectivity check (non-blocking, 3s timeout so it doesn't stall the app).
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
try
{
    var response = await httpClient.GetAsync("http://localhost:4318").ConfigureAwait(false);
    Console.WriteLine($"OTLP endpoint reachable: {(int)response.StatusCode} {response.ReasonPhrase}");
}
catch (TaskCanceledException)
{
    Console.WriteLine("OTLP endpoint connectivity check timed out (dashboard may not be running yet). Continuing anyway...");
}
catch (Exception ex)
{
    Console.WriteLine($"OTLP endpoint not reachable: {ex.Message}. Continuing anyway...");
}

var iteration = 0;
while (true)
{
    iteration++;
    using (var activity = activitySource.StartActivity("HandleRequest", ActivityKind.Server))
    {
        if (iteration == 1)
        {
            Console.WriteLine($"Activity created: {activity != null}, TraceId: {activity?.TraceId}");
        }

        appLogger.LogInformation("Processing request {Iteration} for /api/orders", iteration);

        activity?.SetTag("http.method", "GET");
        activity?.SetTag("http.url", "/api/orders");
        activity?.SetTag("http.status_code", 200);

        await Task.Delay(Random.Shared.Next(10, 50)).ConfigureAwait(false);

        using (var dbActivity = activitySource.StartActivity("DatabaseQuery", ActivityKind.Client))
        {
            dbActivity?.SetTag("db.system", "postgresql");
            dbActivity?.SetTag("db.statement", "SELECT * FROM orders WHERE status = 'active'");
            await Task.Delay(Random.Shared.Next(5, 30)).ConfigureAwait(false);
        }

        using (var cacheActivity = activitySource.StartActivity("CacheCheck", ActivityKind.Client))
        {
            cacheActivity?.SetTag("cache.type", "redis");
            cacheActivity?.SetTag("cache.hit", Random.Shared.Next(0, 2) == 1);
            await Task.Delay(Random.Shared.Next(1, 5)).ConfigureAwait(false);
        }
    }

    requestCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "/api/orders"));
    requestDuration.Record(Random.Shared.Next(15, 100), new KeyValuePair<string, object?>("endpoint", "/api/orders"));

    if (iteration == 1)
    {
        // Force flush on first iteration to verify export works.
        var traceFlushed = tracerProvider!.ForceFlush(5000);
        var meterFlushed = meterProvider!.ForceFlush(5000);
        Console.WriteLine($"\nForceFlush - traces: {traceFlushed}, metrics: {meterFlushed}");
    }

    await Task.Delay(1000).ConfigureAwait(false);
    Console.Write(".");
}

// EventListener that captures OpenTelemetry SDK diagnostics
sealed class OtelEventListener : System.Diagnostics.Tracing.EventListener
{
    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, System.Diagnostics.Tracing.EventLevel.Verbose);
        }
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs eventData)
    {
        // Only print warnings/errors and export-related messages
        if (eventData.Level <= System.Diagnostics.Tracing.EventLevel.Warning ||
            (eventData.Message?.Contains("Export", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            Console.WriteLine($"\n[OTEL-{eventData.Level}] {eventData.Message}");
            if (eventData.Payload != null)
            {
                foreach (var p in eventData.Payload)
                {
                    Console.WriteLine($"  Payload: {p}");
                }
            }
        }
    }
}
