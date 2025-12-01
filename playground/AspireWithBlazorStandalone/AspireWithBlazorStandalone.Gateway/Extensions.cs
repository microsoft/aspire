using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Yarp.ReverseProxy.Configuration;

namespace Microsoft.Extensions.Hosting;

internal static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string DefaultConfigurationEndpointPath = "/_blazor/_configuration";

    private static readonly Dictionary<string, string> s_defaultConfigurationMappings = new()
    {
        ["services"] = "webAssembly:environment",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "webAssembly:environment",
        ["OTEL_EXPORTER_OTLP_HEADERS"] = "webAssembly:environment",
    };

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

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Configures YARP reverse proxy to forward requests from /_api/{service-name}/{**path} to backend services.
    /// </summary>
    public static TBuilder AddServiceProxy<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddReverseProxy()
            .LoadFromMemory(
                routes: GetProxyRoutes(builder.Configuration),
                clusters: GetProxyClusters(builder.Configuration))
            .AddServiceDiscoveryDestinationResolver();

        return builder;
    }

    private static IReadOnlyList<RouteConfig> GetProxyRoutes(IConfiguration configuration)
    {
        var routes = new List<RouteConfig>();
        var servicesSection = configuration.GetSection("services");

        if (!servicesSection.Exists())
        {
            return routes;
        }

        foreach (var serviceSection in servicesSection.GetChildren())
        {
            var serviceName = serviceSection.Key;

            // Create a route for each service: /_api/{serviceName}/{**catch-all}
            routes.Add(new RouteConfig
            {
                RouteId = $"route-{serviceName}",
                ClusterId = $"cluster-{serviceName}",
                Match = new RouteMatch
                {
                    Path = $"/_api/{serviceName}/{{**catch-all}}"
                },
                Transforms = new List<Dictionary<string, string>>
                {
                    new() { ["PathRemovePrefix"] = $"/_api/{serviceName}" }
                }
            });
        }

        return routes;
    }

    private static IReadOnlyList<ClusterConfig> GetProxyClusters(IConfiguration configuration)
    {
        var clusters = new List<ClusterConfig>();
        var servicesSection = configuration.GetSection("services");

        if (!servicesSection.Exists())
        {
            return clusters;
        }

        foreach (var serviceSection in servicesSection.GetChildren())
        {
            var serviceName = serviceSection.Key;

            // Use service discovery URL format for the destination
            clusters.Add(new ClusterConfig
            {
                ClusterId = $"cluster-{serviceName}",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["destination1"] = new DestinationConfig
                    {
                        Address = $"https+http://{serviceName}"
                    }
                }
            });
        }

        return clusters;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app, bool useProxy = false)
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
        app.MapConfigurationEndpoint(DefaultConfigurationEndpointPath, useProxy);

        return app;
    }

    public static IEndpointRouteBuilder MapConfigurationEndpoint(this IEndpointRouteBuilder endpoints, string path, bool useProxy = false)
    {
        return MapConfigurationEndpoint(endpoints, path, s_defaultConfigurationMappings, useProxy);
    }

    public static IEndpointRouteBuilder MapConfigurationEndpoint(this IEndpointRouteBuilder endpoints, string path, Dictionary<string, string> mappings, bool useProxy = false)
    {
        endpoints.MapGet(path, (HttpContext httpContext, IConfiguration configuration) =>
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
                    // If proxy is enabled and this is a service URL, rewrite to proxy URL
                    if (useProxy && sourceKey == "services" && !string.IsNullOrEmpty(value))
                    {
                        var proxyValue = RewriteServiceUrlToProxy(key, value, httpContext);
                        target[key] = proxyValue;
                    }
                    else
                    {
                        target[key] = value;
                    }
                }
            }

            return Results.Json(response);
        });

        return endpoints;
    }

    private static string RewriteServiceUrlToProxy(string configKey, string originalValue, HttpContext httpContext)
    {
        // Config key format: "services:weatherapi:https:0" or "services:weatherapi:http:0"
        // We need to extract the service name and rewrite to proxy URL

        // Parse the key to get the service name
        var parts = configKey.Split(':');
        if (parts.Length < 2 || parts[0] != "services")
        {
            return originalValue;
        }

        var serviceName = parts[1];

        // Build the proxy URL using the current request's scheme and host
        var request = httpContext.Request;
        var proxyBaseUrl = $"{request.Scheme}://{request.Host}/_api/{serviceName}";

        return proxyBaseUrl;
    }

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
