using System.Text.Json.Nodes;
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
    /// Default configuration mappings from source configuration keys to response JSON paths.
    /// Keys are configuration section names, values are dot-separated JSON paths in the response.
    /// </summary>
    private static readonly Dictionary<string, string> s_defaultConfigurationMappings = new()
    {
        ["services"] = "WebAssembly:Environment",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "WebAssembly:Environment",
        ["OTEL_EXPORTER_OTLP_HEADERS"] = "WebAssembly:Environment",
    };

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
        return MapConfigurationEndpoint(endpoints, path, s_defaultConfigurationMappings);
    }

    /// <summary>
    /// Maps the configuration endpoint that exposes configuration to clients based on the provided mappings.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The path for the configuration endpoint.</param>
    /// <param name="mappings">Dictionary mapping source configuration keys to response JSON paths (colon-separated).</param>
    /// <returns>The configured endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapConfigurationEndpoint(this IEndpointRouteBuilder endpoints, string path, Dictionary<string, string> mappings)
    {
        endpoints.MapGet(path, (IConfiguration configuration) =>
        {
            var response = new JsonObject();

            foreach (var (sourceKey, targetPath) in mappings)
            {
                var section = configuration.GetSection(sourceKey);

                // Check if this is a section with children or a single value
                var children = section.AsEnumerable().Where(kvp => !string.IsNullOrEmpty(kvp.Value)).ToList();

                if (children.Count == 0)
                {
                    continue;
                }

                // Get or create the target JsonObject at the specified path
                var target = GetOrCreatePath(response, targetPath);

                foreach (var (key, value) in children)
                {
                    target[key] = value;
                }
            }

            return Results.Json(response);
        });

        return endpoints;
    }

    /// <summary>
    /// Gets or creates a nested JsonObject at the specified colon-separated path.
    /// </summary>
    private static JsonObject GetOrCreatePath(JsonObject root, string path)
    {
        var segments = path.Split(':');
        var current = root;

        foreach (var segment in segments)
        {
            if (!current.ContainsKey(segment))
            {
                current[segment] = new JsonObject();
            }
            current = (JsonObject)current[segment]!;
        }

        return current;
    }
}
