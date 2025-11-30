using System.Diagnostics;
using AspireWithBlazor.ClientServiceDefaults.Telemetry;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private const string ServiceName = "blazorapp-client";

    /// <summary>
    /// Adds Aspire service defaults to a Blazor WebAssembly application.
    /// </summary>
    /// <param name="builder">The WebAssembly host builder.</param>
    /// <returns>The configured WebAssembly host builder.</returns>
    public static WebAssemblyHostBuilder AddBlazorClientServiceDefaults(this WebAssemblyHostBuilder builder)
    {
        builder.ConfigureBlazorClientOpenTelemetry(ServiceName);

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
    /// <param name="serviceName">The service name for telemetry.</param>
    /// <returns>The configured WebAssembly host builder.</returns>
    private static WebAssemblyHostBuilder ConfigureBlazorClientOpenTelemetry(this WebAssemblyHostBuilder builder, string serviceName)
    {

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

        builder.AddBlazorClientOpenTelemetryExporters(serviceName);

        return builder;
    }

    private static WebAssemblyHostBuilder AddBlazorClientOpenTelemetryExporters(this WebAssemblyHostBuilder builder, string serviceName)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var otlpHeaders = builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"];

        // Parse OTLP headers (format: "key1=value1,key2=value2" or "key1=value1")
        var headers = ParseOtlpHeaders(otlpHeaders);

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
            }
            else
            {
                endpoint = new Uri(otlpEndpoint);
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
                            serviceName,
                            headers);

                        return new TaskBasedBatchExportProcessor<Activity>(
                            exporter,
                            maxQueueSize: 2048,
                            scheduledDelayMilliseconds: 5000,
                            exporterTimeoutMilliseconds: 30000,
                            maxExportBatchSize: 512);
                    });
                });

            // Configure metrics with WebAssembly-compatible exporter
            // The standard OpenTelemetry MeterProvider doesn't work in WebAssembly
            // because it requires threading primitives not available in the browser.
            // Our custom exporter uses MeterListener (a .NET runtime feature) instead,
            // which works in the single-threaded WebAssembly environment.
            var metricExporter = new WebAssemblyOtlpMetricExporter(
                new Uri(endpoint, "v1/metrics"),
                serviceName,
                headers);
            builder.Services.AddSingleton(metricExporter);

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
                            serviceName,
                            headers);

                        return new TaskBasedBatchExportProcessor<LogRecord>(
                            exporter,
                            maxQueueSize: 2048,
                            scheduledDelayMilliseconds: 5000,
                            exporterTimeoutMilliseconds: 30000,
                            maxExportBatchSize: 512);
                    });
                });
        }

        return builder;
    }

    /// <summary>
    /// Parses OTLP headers from the standard format "key1=value1,key2=value2".
    /// </summary>
    /// <param name="headersString">The headers string in OTLP format.</param>
    /// <returns>A dictionary of headers, or null if no headers are configured.</returns>
    private static Dictionary<string, string>? ParseOtlpHeaders(string? headersString)
    {
        if (string.IsNullOrWhiteSpace(headersString))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // OTLP headers format: "key1=value1,key2=value2" or just "key1=value1"
        var pairs = headersString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex > 0)
            {
                var key = pair.Substring(0, separatorIndex).Trim();
                var value = pair.Substring(separatorIndex + 1).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    headers[key] = value;
                }
            }
        }

        return headers.Count > 0 ? headers : null;
    }
}
