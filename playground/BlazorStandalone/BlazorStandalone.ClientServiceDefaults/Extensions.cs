using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;

namespace Microsoft.Extensions.Hosting;

public static class BlazorClientExtensions
{
    public static WebAssemblyHostBuilder AddBlazorClientServiceDefaults(this WebAssemblyHostBuilder builder)
    {
        ComponentsMetricsServiceCollectionExtensions.AddComponentsMetrics(builder.Services);
        ComponentsMetricsServiceCollectionExtensions.AddComponentsTracing(builder.Services);

        builder.ConfigureBlazorClientOpenTelemetry();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
        });

        return builder;
    }

    private static WebAssemblyHostBuilder ConfigureBlazorClientOpenTelemetry(this WebAssemblyHostBuilder builder)
    {
        // Without an OTLP path base, there's nowhere to export telemetry in WASM.
        var otlpPathBase = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrEmpty(otlpPathBase))
        {
            return builder;
        }

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"]!;

        // Build a resilience pipeline matching OTLP retry spec behavior:
        //   - Initial backoff: 1s (OTLP default), max 5s
        //   - Exponential backoff with jitter
        //   - Honors Retry-After header from 429/503
        //   - Retryable: 408, 429, 500+ (superset of OTLP's 429/502/503/504)
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(5),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = true,
            })
            .Build();

        // Resolve the OTLP path against the page's origin so telemetry goes through
        // the same origin the user navigated to, avoiding cross-origin issues.
        var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
        var otlpEndpoint = new Uri(baseAddress, $"{otlpPathBase}/");

        // Wire HttpClientFactory for all OTLP exporter instances via IPostConfigureOptions.
        // This runs during options resolution for all 3 signals (traces, metrics, logging),
        // and has access to the DI container to resolve ILoggerFactory.
        builder.Services.AddSingleton<IPostConfigureOptions<OtlpExporterOptions>>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Aspire.OtlpExport");
            return new PostConfigureOptions<OtlpExporterOptions>(null, o =>
            {
                o.HttpClientFactory = () => new HttpClient(new BackgroundExportHandler(pipeline, logger));
            });
        });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            // Use a fixed instanceId so all browser tabs report as a single service instance
            // in the dashboard rather than spawning separate entries per tab.
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName, serviceInstanceId: serviceName));
            logging.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint, "v1/logs"));
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceInstanceId: serviceName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("Microsoft.AspNetCore.Components");
                metrics.AddMeter("Microsoft.AspNetCore.Components.Lifecycle");
                metrics.AddHttpClientInstrumentation();
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint, "v1/metrics"));
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource("Microsoft.AspNetCore.Components")
                    .AddHttpClientInstrumentation();
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint, "v1/traces"));
            });

        return builder;
    }
}
