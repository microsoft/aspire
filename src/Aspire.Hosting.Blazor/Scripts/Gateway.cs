// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Extensions.Http.Resilience@10.0.0
#:package Microsoft.Extensions.ServiceDiscovery@10.0.0
#:package Microsoft.Extensions.ServiceDiscovery.Yarp@10.0.0
#:package OpenTelemetry.Exporter.OpenTelemetryProtocol@1.14.0
#:package OpenTelemetry.Extensions.Hosting@1.14.0
#:package OpenTelemetry.Instrumentation.AspNetCore@1.14.0
#:package OpenTelemetry.Instrumentation.Http@1.14.0
#:package OpenTelemetry.Instrumentation.Runtime@1.14.0
#:package Yarp.ReverseProxy@2.3.0
#:property PublishAot=false
#:property ManagePackageVersionsCentrally=false
#:property EnforceCodeStyleInBuild=false
#:property _SkipUpgradeNetAnalyzersNuGetWarning=true

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Load the merged runtime manifest (produced by Aspire orchestration).
// Hosting emits the path via the standard "staticWebAssets" config key.
builder.WebHost.UseStaticWebAssets();

// Read ClientApps configuration section (injected by Aspire hosting integration)
var appConfigs = builder.Configuration.GetSection("ClientApps").Get<Dictionary<string, ClientAppConfiguration>>() ?? [];

// Load YARP proxy config from configuration (emitted as env vars by Aspire hosting)
var proxySection = builder.Configuration.GetSection("ReverseProxy");
var hasProxy = proxySection.Exists();

if (hasProxy)
{
    builder.Services.AddReverseProxy()
        .LoadFromConfig(proxySection)
        .AddServiceDiscoveryDestinationResolver();
}

var app = builder.Build();

app.MapDefaultEndpoints();

if (hasProxy)
{
    app.MapReverseProxy();
}

// Per-app: configuration endpoint + static asset serving.
foreach (var appConfig in appConfigs.Values)
{
    if (!string.IsNullOrEmpty(appConfig.ConfigEndpointPath) && !string.IsNullOrEmpty(appConfig.ConfigResponse))
    {
        app.MapGet(appConfig.ConfigEndpointPath, () => Results.Content(appConfig.ConfigResponse, "application/json"))
            .WithMetadata(new ContentEncodingMetadata("identity", 1.0));
    }

    if (!string.IsNullOrEmpty(appConfig.EndpointsManifest) && File.Exists(appConfig.EndpointsManifest))
    {
        app.MapGroup(appConfig.PathPrefix!).MapStaticAssets(appConfig.EndpointsManifest)
            .Add(ep =>
            {
                if (ep is RouteEndpointBuilder reb && reb.RoutePattern.RawText?.Contains("{**path") == true)
                {
                    reb.Order = int.MaxValue;
                }
            });
    }
}

app.Run();

sealed class ClientAppConfiguration
{
    public string? PathPrefix { get; set; }
    public string? EndpointsManifest { get; set; }
    public string? ConfigEndpointPath { get; set; }
    public string? ConfigResponse { get; set; }
}

static class ServiceDefaultsExtensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
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

        // Suppress verbose per-request logs from ASP.NET Core and HttpClient
        // for static assets, OTLP ingestion, and OTLP export to keep the
        // dashboard focused on application-level activity.
        builder.Logging.AddFilter("Microsoft.AspNetCore.StaticAssets", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient.OtlpExporter", LogLevel.Warning);

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
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health")
                            && !context.Request.Path.StartsWithSegments("/alive")
                            && !IsStaticAssetOrOtlpRequest(context.Request.Path)
                    )
                    .AddHttpClientInstrumentation(options =>
                        // Filter out the gateway's own OTLP export calls to the dashboard
                        // to prevent a feedback loop (exporting traces creates new traces).
                        options.FilterHttpRequestMessage = request =>
                            request.RequestUri is null
                            || !request.RequestUri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal)
                    );
            });

        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    private static bool IsStaticAssetOrOtlpRequest(PathString path)
    {
        var pathValue = path.Value;
        return pathValue is not null
            && (pathValue.Contains("/_framework/", StringComparison.Ordinal)
                || pathValue.Contains("/_content/", StringComparison.Ordinal)
                || pathValue.Contains("/_otlp/", StringComparison.Ordinal));
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }
}

static class EndpointExtensions
{
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks("/health");
            app.MapHealthChecks("/alive", new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
