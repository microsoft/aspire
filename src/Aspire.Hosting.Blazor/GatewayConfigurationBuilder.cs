// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Builds YARP reverse proxy configuration and per-app Blazor WASM client configuration
/// as environment variables for the gateway process.
/// </summary>
internal static class GatewayConfigurationBuilder
{
    /// <summary>
    /// Emits YARP route/cluster configuration and per-app client configuration as environment
    /// variables. The gateway reads these via <c>LoadFromConfig</c> at startup.
    /// </summary>
    public static void EmitProxyConfiguration(
        IDictionary<string, object> env,
        List<GatewayAppRegistration> apps,
        EndpointReference gatewayEndpoint,
        EndpointReference? httpGatewayEndpoint = null,
        object? httpOtlpEndpoint = null)
    {
        var addedClusters = new HashSet<string>();
        var httpClientEndpoint = httpGatewayEndpoint ?? (gatewayEndpoint.IsHttp ? gatewayEndpoint : null);
        var httpsClientEndpoint = gatewayEndpoint.IsHttps ? gatewayEndpoint : null;

        foreach (var reg in apps)
        {
            var prefix = reg.PathPrefix;
            var envPrefix = $"ClientApps__{reg.Resource.Name}";

            // Flatten services: one HostedClientService per named endpoint so each gets
            // its own YARP cluster destination.
            var servicesList = new List<HostedClientService>();
            foreach (var svc in reg.Services)
            {
                if (svc.UseAllEndpoints)
                {
                    servicesList.Add(new HostedClientService(svc.Name, reg.ApiPrefix));
                }
                else
                {
                    foreach (var endpointName in svc.EndpointNames)
                    {
                        servicesList.Add(new HostedClientService(svc.Name, reg.ApiPrefix, endpointName));
                    }
                }
            }
            var services = servicesList.ToArray();

            env[$"{envPrefix}__ConfigResponse"] = BuildConfigExpression(
                httpClientEndpoint,
                httpsClientEndpoint,
                prefix,
                reg.Resource.Name,
                services,
                reg.ProxyBlazorTelemetry,
                httpOtlpEndpoint,
                reg.OtlpPrefix);

            EmitYarpRoutes(env, prefix, reg.Resource.Name, services, reg.ProxyBlazorTelemetry, addedClusters,
                reg.OtlpPrefix, httpOtlpEndpoint);
        }

        if (apps.Any(app => app.ProxyBlazorTelemetry))
        {
            EmitOtlpCluster(env, httpOtlpEndpoint);
        }
    }

    /// <summary>
    /// Emits YARP route/cluster and client configuration for a hosted Blazor app
    /// (no path prefix, telemetry optional).
    /// </summary>
    public static void EmitHostedProxyConfiguration(
        IDictionary<string, object> env,
        EndpointReference hostEndpoint,
        EndpointReference? httpHostEndpoint,
        string resourceName,
        IReadOnlyList<HostedClientService> services,
        bool proxyBlazorTelemetry,
        object? httpOtlpEndpoint,
        string otlpPrefix = DefaultOtlpPrefix)
    {
        var httpClientEndpoint = httpHostEndpoint ?? (hostEndpoint.IsHttp ? hostEndpoint : null);
        var httpsClientEndpoint = hostEndpoint.IsHttps ? hostEndpoint : null;

        env["Client__ConfigResponse"] = BuildConfigExpression(
            httpClientEndpoint,
            httpsClientEndpoint,
            prefix: null,
            resourceName,
            services,
            proxyBlazorTelemetry,
            httpOtlpEndpoint,
            otlpPrefix);
        env["Client__ConfigEndpointPath"] = "/_blazor/_configuration";

        EmitYarpRoutes(env, prefix: null, resourceName, services, proxyBlazorTelemetry, addedClusters: null,
            otlpPrefix, httpOtlpEndpoint);

        if (proxyBlazorTelemetry)
        {
            EmitOtlpCluster(env, httpOtlpEndpoint);
        }
    }

    /// <summary>
    /// Default URL path segment for proxying API requests to backend services.
    /// </summary>
    internal const string DefaultApiPrefix = "_api";

    /// <summary>
    /// Default URL path segment for proxying OTLP telemetry to the Aspire dashboard.
    /// </summary>
    internal const string DefaultOtlpPrefix = "_otlp";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static void EmitYarpRoutes(
        IDictionary<string, object> env,
        string? prefix,
        string resourceName,
        IReadOnlyList<HostedClientService> services,
        bool proxyBlazorTelemetry,
        HashSet<string>? addedClusters,
        string otlpPrefix = DefaultOtlpPrefix,
        object? httpOtlpEndpoint = null)
    {
        var pathBase = prefix != null ? $"/{prefix}" : "";

        foreach (var svc in services)
        {
            var routeId = prefix != null ? $"route-{resourceName}-{svc.ServiceName}" : $"route-{svc.ServiceName}";
            var clusterId = $"cluster-{svc.ServiceName}";

            env[$"ReverseProxy__Routes__{routeId}__ClusterId"] = clusterId;
            env[$"ReverseProxy__Routes__{routeId}__Match__Path"] = $"{pathBase}/{svc.ApiPrefix}/{svc.ServiceName}/{{**catch-all}}";
            env[$"ReverseProxy__Routes__{routeId}__Transforms__0__PathRemovePrefix"] = $"{pathBase}/{svc.ApiPrefix}/{svc.ServiceName}";

            // Use endpoint name as destination ID for named endpoints so multiple named
            // endpoints on the same service each get their own destination in the cluster.
            var destId = svc.EndpointName ?? "d1";
            if (addedClusters == null || addedClusters.Add($"{clusterId}__{destId}"))
            {
                env[$"ReverseProxy__Clusters__{clusterId}__Destinations__{destId}__Address"] = svc.DestinationAddress;
            }
        }

        if (proxyBlazorTelemetry && httpOtlpEndpoint is not null)
        {
            var otlpRouteId = prefix != null ? $"route-otlp-{resourceName}" : "route-otlp";
            env[$"ReverseProxy__Routes__{otlpRouteId}__ClusterId"] = "cluster-otlp-dashboard";
            env[$"ReverseProxy__Routes__{otlpRouteId}__Match__Path"] = $"{pathBase}/{otlpPrefix}/{{**catch-all}}";
            env[$"ReverseProxy__Routes__{otlpRouteId}__Transforms__0__PathRemovePrefix"] = $"{pathBase}/{otlpPrefix}";

            if (env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var headersObj) && headersObj is string headersStr)
            {
                var transformIndex = 1;
                foreach (var header in headersStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = header.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        env[$"ReverseProxy__Routes__{otlpRouteId}__Transforms__{transformIndex}__RequestHeader"] = parts[0].Trim();
                        env[$"ReverseProxy__Routes__{otlpRouteId}__Transforms__{transformIndex}__Set"] = parts[1].Trim();
                        transformIndex++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Emits the shared OTLP dashboard YARP cluster.
    /// Uses the HTTP OTLP endpoint (for HTTP/protobuf from WASM clients) when available.
    /// Accepts either a string URL or an EndpointReference (which resolves at runtime).
    /// Does NOT fall back to OTEL_EXPORTER_OTLP_ENDPOINT because that is typically the
    /// gRPC endpoint which cannot be used from browser-based clients.
    /// </summary>
    private static void EmitOtlpCluster(IDictionary<string, object> env, object? httpOtlpEndpoint = null)
    {
        if (httpOtlpEndpoint is not null)
        {
            env["ReverseProxy__Clusters__cluster-otlp-dashboard__Destinations__d1__Address"] = httpOtlpEndpoint;
        }
    }

    // All string values placed into the JSON are constructed by us: a brace-free token
    // (__ORIGIN__) concatenated with URL path segments (e.g. "/app/_api/weatherapi").
    // Because none of these values contain { or }, the only literal braces in the
    // serialized output are structural JSON. This invariant makes the Replace("{","{{")
    // step below correct — if a future change introduces braces in values, the
    // string.Format template would break and tests would catch it immediately.
    private const string OriginToken = "__ORIGIN__";

    /// <summary>
    /// Builds the ConfigResponse JSON as a <see cref="ReferenceExpression"/>. In dev mode the
    /// gateway origin resolves to the actual URL; in publish mode publishers emit a placeholder.
    /// </summary>
    internal static ReferenceExpression BuildConfigExpression(
        EndpointReference? httpEndpoint,
        EndpointReference? httpsEndpoint,
        string? prefix,
        string resourceName,
        IReadOnlyList<HostedClientService> services,
        bool proxyBlazorTelemetry,
        object? httpOtlpEndpoint,
        string otlpPrefix = DefaultOtlpPrefix)
    {
        var pathBase = prefix != null ? $"/{prefix}" : "";

        var environment = new JsonObject();

        foreach (var svc in services)
        {
            if (httpsEndpoint is not null)
            {
                environment[$"services__{svc.ServiceName}__https__0"] = $"{OriginToken}{pathBase}/{svc.ApiPrefix}/{svc.ServiceName}";
            }

            if (httpEndpoint is not null)
            {
                environment[$"services__{svc.ServiceName}__http__0"] = $"{OriginToken}{pathBase}/{svc.ApiPrefix}/{svc.ServiceName}";
            }
        }

        if (proxyBlazorTelemetry && httpOtlpEndpoint is not null)
        {
            environment["OTEL_SERVICE_NAME"] = resourceName;

            // Send only the OTLP path so the WASM client resolves it against its own
            // page origin (HostEnvironment.BaseAddress). This avoids cross-origin issues
            // when the user navigates via HTTP but the gateway also exposes HTTPS.
            environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"{pathBase}/{otlpPrefix}";
            environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

            // NOTE: OTEL_EXPORTER_OTLP_HEADERS is intentionally NOT sent to the WASM client.
            // The headers contain the dashboard OTLP API key, and this config is delivered
            // to browser-visible JSON. The YARP proxy injects the headers server-side when
            // forwarding telemetry to the dashboard.
        }

        var config = new JsonObject
        {
            ["webAssembly"] = new JsonObject
            {
                ["environment"] = environment
            }
        };

        var json = config.ToJsonString(s_jsonOptions);

        // The only literal { } in the output are structural JSON braces (we control
        // all string values and they contain no braces). Escape them for string.Format,
        // then swap the origin token for {0}.
        var format = json
            .Replace("{", "{{")
            .Replace("}", "}}")
            .Replace(OriginToken, "{0}");

        var originEndpoint = httpsEndpoint ?? httpEndpoint
            ?? throw new InvalidOperationException("At least one gateway endpoint (HTTP or HTTPS) must be provided.");

        var originRef = new GatewayOriginReference(originEndpoint);
        var builder = new ReferenceExpressionBuilder();
        var segments = format.Split("{0}");
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                builder.AppendFormatted(originRef);
            }
            builder.AppendLiteral(segments[i]);
        }

        return builder.Build();
    }
}
