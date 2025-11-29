using System.Diagnostics;
using AspireWithBlazor.ClientServiceDefaults.Telemetry;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds common .NET Aspire services for Blazor WebAssembly clients: service discovery, resilience, and OpenTelemetry.
/// This project should be referenced by the Blazor WebAssembly client project.
/// </summary>
public static class BlazorClientExtensions
{
    /// <summary>
    /// Adds Aspire service defaults to a Blazor WebAssembly application.
    /// </summary>
    /// <param name="builder">The WebAssembly host builder.</param>
    /// <returns>The configured WebAssembly host builder.</returns>
    public static WebAssemblyHostBuilder AddBlazorClientServiceDefaults(this WebAssemblyHostBuilder builder)
    {
        builder.ConfigureBlazorClientOpenTelemetry();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry for the Blazor WebAssembly application.
    /// Telemetry is sent via HTTP/Protobuf through the gateway.
    /// </summary>
    /// <param name="builder">The WebAssembly host builder.</param>
    /// <returns>The configured WebAssembly host builder.</returns>
    public static WebAssemblyHostBuilder ConfigureBlazorClientOpenTelemetry(this WebAssemblyHostBuilder builder)
    {
        // Get service name from configuration or fall back to environment name
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] 
            ?? builder.HostEnvironment.Environment;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics =>
            {
                metrics.AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Add Blazor component metrics
                    // See: https://learn.microsoft.com/aspnet/core/blazor/performance#metrics-and-tracing
                    .AddMeter("Microsoft.AspNetCore.Components")
                    .AddMeter("Microsoft.AspNetCore.Components.Lifecycle");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(serviceName)
                    .AddHttpClientInstrumentation()
                    // Add Blazor component tracing
                    .AddSource("Microsoft.AspNetCore.Components");
            });

        builder.AddBlazorClientOpenTelemetryExporters();

        return builder;
    }

    private static WebAssemblyHostBuilder AddBlazorClientOpenTelemetryExporters(this WebAssemblyHostBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] 
            ?? builder.HostEnvironment.Environment;

        Console.WriteLine($"[BlazorOTel] OTEL_EXPORTER_OTLP_ENDPOINT from config: '{otlpEndpoint ?? "(null)"}'");
        Console.WriteLine($"[BlazorOTel] Service name: '{serviceName}'");

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // The endpoint may be relative (/_otlp) or absolute (https://localhost:21187)
            // For relative URLs, construct the full URL using the app's base address
            Uri endpoint;
            if (otlpEndpoint.StartsWith("/"))
            {
                // Relative URL - combine with app's base address
                var baseUri = new Uri(builder.HostEnvironment.BaseAddress);
                endpoint = new Uri(baseUri, otlpEndpoint);
                Console.WriteLine($"[BlazorOTel] Using proxy endpoint: {endpoint}");
            }
            else
            {
                endpoint = new Uri(otlpEndpoint);
                Console.WriteLine($"[BlazorOTel] Using direct dashboard endpoint: {endpoint}");
            }

            // Configure tracing with WebAssembly-compatible exporter
            // The custom exporter uses truly async HTTP calls without blocking,
            // which is required because WebAssembly is single-threaded and the
            // standard OtlpTraceExporter uses blocking patterns that cause deadlock.
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing =>
                {
                    tracing.AddProcessor(sp =>
                    {
                        var exporter = new WebAssemblyOtlpTraceExporter(
                            new Uri(endpoint, "v1/traces"),
                            serviceName);

                        return new TaskBasedBatchExportProcessor<Activity>(
                            exporter,
                            maxQueueSize: 2048,
                            scheduledDelayMilliseconds: 5000,
                            exporterTimeoutMilliseconds: 30000,
                            maxExportBatchSize: 512);
                    });
                });

            // Configure metrics with simple periodic exporting (simpler for WASM)
            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddOtlpExporter((exporterOptions, readerOptions) =>
                    {
                        exporterOptions.Endpoint = new Uri(endpoint, "v1/metrics");
                        exporterOptions.Protocol = OtlpExportProtocol.HttpProtobuf;
                        // Use a longer interval to reduce background work
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10000;
                    });
                });

            // Configure logging with WebAssembly-compatible exporter
            // The custom exporter uses truly async HTTP calls without blocking,
            // which is required because WebAssembly is single-threaded and the
            // standard OtlpLogExporter uses blocking patterns that cause deadlock.
            builder.Services.AddOpenTelemetry()
                .WithLogging(logging =>
                {
                    logging.AddProcessor(sp =>
                    {
                        var exporter = new WebAssemblyOtlpLogExporter(
                            new Uri(endpoint, "v1/logs"),
                            serviceName);

                        return new TaskBasedBatchExportProcessor<LogRecord>(
                            exporter,
                            maxQueueSize: 2048,
                            scheduledDelayMilliseconds: 5000,
                            exporterTimeoutMilliseconds: 30000,
                            maxExportBatchSize: 512);
                    });
                });

            Console.WriteLine($"[BlazorOTel] Configured OTLP exporter with HttpProtobuf to: {endpoint}");
        }
        else
        {
            Console.WriteLine("[BlazorOTel] OTEL_EXPORTER_OTLP_ENDPOINT not configured, telemetry export disabled");
        }

        return builder;
    }
}
