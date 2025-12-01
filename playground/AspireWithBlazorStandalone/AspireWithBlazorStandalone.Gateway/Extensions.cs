using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Microsoft.Extensions.Hosting;

internal static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string DefaultConfigurationEndpointPath = "/_blazor/_configuration";
    private const string OtlpProxyPath = "/_otlp";
    private const string OtlpClusterId = "cluster-otlp-dashboard";

    private static readonly Dictionary<string, string> s_defaultConfigurationMappings = new()
    {
        ["services"] = "webAssembly:environment",
        // Note: OTEL config is handled specially when proxy is enabled
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "webAssembly:environment",
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
    /// Also configures OTLP telemetry proxy at /_otlp/{**path} to forward to the Dashboard OTLP endpoint.
    /// </summary>
    public static TBuilder AddServiceProxy<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var (routes, clusters) = GetProxyConfiguration(builder.Configuration);

        builder.Services.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(context =>
            {
                // Add OTLP API key header for OTLP proxy requests
                if (context.Route.RouteId == "route-otlp")
                {
                    var otlpHeaders = builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"];
                    if (!string.IsNullOrEmpty(otlpHeaders))
                    {
                        // Parse "x-otlp-api-key=value" format
                        foreach (var header in otlpHeaders.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = header.Split('=', 2);
                            if (parts.Length == 2)
                            {
                                context.AddRequestTransform(transformContext =>
                                {
                                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
                                    return ValueTask.CompletedTask;
                                });
                            }
                        }
                    }
                }
            });

        return builder;
    }

    private static (IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters) GetProxyConfiguration(IConfiguration configuration)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        // Add service routes
        AddServiceProxyRoutes(configuration, routes, clusters);

        // Add OTLP proxy route
        AddOtlpProxyRoute(configuration, routes, clusters);

        return (routes, clusters);
    }

    private static void AddServiceProxyRoutes(IConfiguration configuration, List<RouteConfig> routes, List<ClusterConfig> clusters)
    {
        var servicesSection = configuration.GetSection("services");

        if (!servicesSection.Exists())
        {
            return;
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
    }

    private static void AddOtlpProxyRoute(IConfiguration configuration, List<RouteConfig> routes, List<ClusterConfig> clusters)
    {
        // Get the OTLP endpoint from configuration (set by Aspire host)
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (string.IsNullOrEmpty(otlpEndpoint))
        {
            return;
        }

        // Add route for OTLP proxy: /_otlp/{**catch-all} -> Dashboard OTLP endpoint
        routes.Add(new RouteConfig
        {
            RouteId = "route-otlp",
            ClusterId = OtlpClusterId,
            Match = new RouteMatch
            {
                Path = $"{OtlpProxyPath}/{{**catch-all}}"
            },
            Transforms = new List<Dictionary<string, string>>
            {
                new() { ["PathRemovePrefix"] = OtlpProxyPath }
            }
        });

        // Add cluster for Dashboard OTLP endpoint
        clusters.Add(new ClusterConfig
        {
            ClusterId = OtlpClusterId,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["dashboard-otlp"] = new DestinationConfig
                {
                    Address = otlpEndpoint
                }
            }
        });
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
                    // If proxy is enabled, rewrite OTLP endpoint to use the gateway proxy
                    else if (useProxy && sourceKey == "OTEL_EXPORTER_OTLP_ENDPOINT" && !string.IsNullOrEmpty(value))
                    {
                        // Rewrite to gateway-relative OTLP proxy endpoint
                        var request = httpContext.Request;
                        var proxyOtlpEndpoint = $"{request.Scheme}://{request.Host}{OtlpProxyPath}";
                        target[key] = proxyOtlpEndpoint;
                        // Also add the protocol since browser needs HTTP (not gRPC)
                        target["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
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
