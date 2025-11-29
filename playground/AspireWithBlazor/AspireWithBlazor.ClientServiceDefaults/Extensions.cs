using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
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
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(serviceName)
                    .AddHttpClientInstrumentation();
            });

        builder.AddBlazorClientOpenTelemetryExporters();

        return builder;
    }

    private static WebAssemblyHostBuilder AddBlazorClientOpenTelemetryExporters(this WebAssemblyHostBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // WebAssembly does not support gRPC, so we must use HTTP/Protobuf
            // The OTLP exporter will append signal-specific paths: /v1/traces, /v1/metrics, /v1/logs
            builder.Services.AddOpenTelemetry()
                .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, new Uri(otlpEndpoint));

            Console.WriteLine($"[BlazorOTel] Configured OTLP exporter with HttpProtobuf to: {otlpEndpoint}");
        }
        else
        {
            Console.WriteLine("[BlazorOTel] OTEL_EXPORTER_OTLP_ENDPOINT not configured, telemetry export disabled");
        }

        return builder;
    }
}
