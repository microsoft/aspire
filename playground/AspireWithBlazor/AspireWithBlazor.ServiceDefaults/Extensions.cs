using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
/// This project should be referenced by each service project in your solution.
/// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string DefaultConfigurationEndpointPath = "/_blazor/_configuration";

    /// <summary>
    /// Adds common .NET Aspire services to the application.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the host application builder.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The configured host application builder.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

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
    /// Configures OpenTelemetry for the application.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the host application builder.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The configured host application builder.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Adds default health checks to the application.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the host application builder.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The configured host application builder.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps default endpoints including health checks and the configuration endpoint for WebAssembly clients.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The configured web application.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        // Map the configuration endpoint for WebAssembly clients
        app.MapConfigurationEndpoint(DefaultConfigurationEndpointPath);

        return app;
    }

    /// <summary>
    /// Maps the configuration endpoint that exposes service discovery and telemetry configuration to WebAssembly clients.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The path for the configuration endpoint.</param>
    /// <returns>The configured endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapConfigurationEndpoint(this IEndpointRouteBuilder endpoints, string path)
    {
        endpoints.MapGet(path, async (HttpContext context) =>
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();

            var config = new BlazorClientConfiguration();

            // Flow OTLP endpoint for WebAssembly telemetry
            var otelEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
            if (!string.IsNullOrWhiteSpace(otelEndpoint))
            {
                config.WebAssembly.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = otelEndpoint;
            }

            // Flow OTLP authentication headers (x-otlp-api-key) for dashboard authentication
            var otelHeaders = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
            if (!string.IsNullOrWhiteSpace(otelHeaders))
            {
                config.WebAssembly.Environment["OTEL_EXPORTER_OTLP_HEADERS"] = otelHeaders;
            }

            // Build WebAssembly environment variables from service discovery configuration
            // Format: services__{servicename}__{scheme}__{index} = url
            var servicesSection = configuration.GetSection("services");
            foreach (var service in servicesSection.GetChildren())
            {
                foreach (var endpoint in service.GetChildren())
                {
                    var index = 0;
                    foreach (var url in endpoint.GetChildren())
                    {
                        var envKey = $"services__{service.Key}__{endpoint.Key}__{index}";
                        var envValue = url.Value ?? "";
                        config.WebAssembly.Environment[envKey] = envValue;
                        index++;
                    }
                    // Handle single value case
                    if (index == 0 && !string.IsNullOrEmpty(endpoint.Value))
                    {
                        var envKey = $"services__{service.Key}__{endpoint.Key}__0";
                        config.WebAssembly.Environment[envKey] = endpoint.Value;
                    }
                }
            }

            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, config, BlazorClientConfigurationContext.Default.BlazorClientConfiguration);
        });

        return endpoints;
    }
}

/// <summary>
/// Configuration model exposed to Blazor WebAssembly clients.
/// </summary>
public sealed class BlazorClientConfiguration
{
    /// <summary>
    /// Gets or sets the service URL mappings.
    /// Keys are service names, values are gateway-relative paths.
    /// </summary>
    public Dictionary<string, string> Services { get; set; } = [];

    /// <summary>
    /// Gets or sets the WebAssembly-specific configuration including environment variables.
    /// </summary>
    public WebAssemblyConfiguration WebAssembly { get; set; } = new();
}

/// <summary>
/// WebAssembly-specific configuration.
/// </summary>
public sealed class WebAssemblyConfiguration
{
    /// <summary>
    /// Gets or sets the environment variables to inject into the WebAssembly runtime.
    /// </summary>
    public Dictionary<string, string> Environment { get; set; } = [];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BlazorClientConfiguration))]
internal sealed partial class BlazorClientConfigurationContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
