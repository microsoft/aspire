// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
        EndpointReference? httpGatewayEndpoint = null)
    {
        var addedClusters = new HashSet<string>();
        var httpClientEndpoint = httpGatewayEndpoint ?? (gatewayEndpoint.IsHttp ? gatewayEndpoint : null);
        var httpsClientEndpoint = gatewayEndpoint.IsHttps ? gatewayEndpoint : null;

        // Capture OTLP headers so the WASM client can send them directly
        env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var otlpHeaders);

        foreach (var reg in apps)
        {
            var prefix = reg.PathPrefix;
            var envPrefix = $"ClientApps__{reg.Resource.Name}";

            // Per-app client config: use an IValueProvider that resolves the gateway URL
            // at startup and builds the final JSON response.
            env[$"{envPrefix}__ConfigResponse"] = new ClientConfigValueProvider(
                gatewayEndpoint,
                httpClientEndpoint,
                httpsClientEndpoint,
                prefix,
                reg.Resource.Name,
                reg.ServiceNames,
                reg.ProxyTelemetry,
                otlpHeaders,
                reg.ApiPrefix,
                reg.OtlpPrefix);

            EmitYarpRoutes(env, prefix, reg.Resource.Name, reg.ServiceNames, reg.ProxyTelemetry, addedClusters,
                reg.ApiPrefix, reg.OtlpPrefix);
        }

        if (apps.Any(app => app.ProxyTelemetry))
        {
            EmitOtlpCluster(env);
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
        string[] serviceNames,
        bool proxyTelemetry,
        string? httpOtlpEndpointUrl,
        string apiPrefix = DefaultApiPrefix,
        string otlpPrefix = DefaultOtlpPrefix)
    {
        var httpClientEndpoint = httpHostEndpoint ?? (hostEndpoint.IsHttp ? hostEndpoint : null);
        var httpsClientEndpoint = hostEndpoint.IsHttps ? hostEndpoint : null;

        // Capture OTLP headers so the WASM client can send them directly
        env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var otlpHeaders);

        env["Client__ConfigResponse"] = new ClientConfigValueProvider(
            hostEndpoint,
            httpClientEndpoint,
            httpsClientEndpoint,
            prefix: null,
            resourceName,
            serviceNames,
            proxyTelemetry,
            otlpHeaders,
            apiPrefix,
            otlpPrefix);
        env["Client__ConfigEndpointPath"] = "/_blazor/_configuration";

        EmitYarpRoutes(env, prefix: null, resourceName, serviceNames, proxyTelemetry, addedClusters: null,
            apiPrefix, otlpPrefix);

        if (proxyTelemetry)
        {
            EmitOtlpCluster(env, httpOtlpEndpointUrl);
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

    private static void EmitYarpRoutes(
        IDictionary<string, object> env,
        string? prefix,
        string resourceName,
        string[] serviceNames,
        bool proxyTelemetry,
        HashSet<string>? addedClusters,
        string apiPrefix = DefaultApiPrefix,
        string otlpPrefix = DefaultOtlpPrefix)
    {
        var pathBase = prefix != null ? $"/{prefix}" : "";

        foreach (var svc in serviceNames)
        {
            var routeId = prefix != null ? $"route-{resourceName}-{svc}" : $"route-{svc}";
            var clusterId = $"cluster-{svc}";

            env[$"ReverseProxy__Routes__{routeId}__ClusterId"] = clusterId;
            env[$"ReverseProxy__Routes__{routeId}__Match__Path"] = $"{pathBase}/{apiPrefix}/{svc}/{{**catch-all}}";
            env[$"ReverseProxy__Routes__{routeId}__Transforms__0__PathRemovePrefix"] = $"{pathBase}/{apiPrefix}/{svc}";

            if (addedClusters == null || addedClusters.Add(clusterId))
            {
                env[$"ReverseProxy__Clusters__{clusterId}__Destinations__d1__Address"] = $"https+http://{svc}";
            }
        }

        if (proxyTelemetry)
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
    /// Prefers the HTTP OTLP endpoint (for HTTP/protobuf from WASM clients) when available;
    /// falls back to OTEL_EXPORTER_OTLP_ENDPOINT (typically the gRPC endpoint).
    /// </summary>
    private static void EmitOtlpCluster(IDictionary<string, object> env, string? httpOtlpEndpointUrl = null)
    {
        if (httpOtlpEndpointUrl != null)
        {
            env["ReverseProxy__Clusters__cluster-otlp-dashboard__Destinations__d1__Address"] = httpOtlpEndpointUrl;
        }
        else if (env.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpoint))
        {
            env["ReverseProxy__Clusters__cluster-otlp-dashboard__Destinations__d1__Address"] = otlpEndpoint;
        }
    }

    /// <summary>
    /// An IValueProvider that resolves an endpoint URL and builds the
    /// Blazor WASM configuration JSON response. At run time, the URL is
    /// resolved from the EndpointReference. At publish time, ValueExpression emits
    /// the JSON with manifest expression placeholders for the deployer to resolve.
    /// Used by both the standalone gateway and hosted Blazor models.
    /// </summary>
    internal sealed class ClientConfigValueProvider(
        EndpointReference primaryEndpoint,
        EndpointReference? httpEndpoint,
        EndpointReference? httpsEndpoint,
        string? prefix,
        string resourceName,
        string[] serviceNames,
        bool proxyTelemetry,
        object? otlpHeaders,
        string apiPrefix = DefaultApiPrefix,
        string otlpPrefix = DefaultOtlpPrefix) : IValueProvider, IManifestExpressionProvider
    {
        string IManifestExpressionProvider.ValueExpression =>
            BuildJson(
                ((IManifestExpressionProvider)primaryEndpoint).ValueExpression,
                ResolveEndpointExpression(httpEndpoint),
                ResolveEndpointExpression(httpsEndpoint),
                ResolveHeadersExpression());

        async ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
        {
            var primaryUrl = await primaryEndpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
            var httpUrl = await ResolveEndpointAsync(httpEndpoint, cancellationToken).ConfigureAwait(false);
            var httpsUrl = await ResolveEndpointAsync(httpsEndpoint, cancellationToken).ConfigureAwait(false);
            var headers = await ResolveHeadersAsync(cancellationToken).ConfigureAwait(false);
            return BuildJson(primaryUrl, httpUrl, httpsUrl, headers);
        }

        async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
        {
            var primaryUrl = await primaryEndpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
            var httpUrl = await ResolveEndpointAsync(httpEndpoint, context, cancellationToken).ConfigureAwait(false);
            var httpsUrl = await ResolveEndpointAsync(httpsEndpoint, context, cancellationToken).ConfigureAwait(false);
            var headers = await ResolveHeadersAsync(context, cancellationToken).ConfigureAwait(false);
            return BuildJson(primaryUrl, httpUrl, httpsUrl, headers);
        }

        private async ValueTask<string?> ResolveHeadersAsync(CancellationToken cancellationToken)
        {
            if (otlpHeaders is IValueProvider vp)
            {
                return await vp.GetValueAsync(cancellationToken).ConfigureAwait(false);
            }
            return otlpHeaders as string;
        }

        private async ValueTask<string?> ResolveHeadersAsync(ValueProviderContext context, CancellationToken cancellationToken)
        {
            if (otlpHeaders is IValueProvider vp)
            {
                return await vp.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
            }
            return otlpHeaders as string;
        }

        private static async ValueTask<string?> ResolveEndpointAsync(EndpointReference? endpoint, CancellationToken cancellationToken)
        {
            if (endpoint is null)
            {
                return null;
            }

            return await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask<string?> ResolveEndpointAsync(EndpointReference? endpoint, ValueProviderContext context, CancellationToken cancellationToken)
        {
            if (endpoint is null)
            {
                return null;
            }

            return await endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        }

        private static string? ResolveEndpointExpression(EndpointReference? endpoint)
        {
            return endpoint is IManifestExpressionProvider manifestExpressionProvider
                ? manifestExpressionProvider.ValueExpression
                : null;
        }

        private string? ResolveHeadersExpression()
        {
            if (otlpHeaders is IManifestExpressionProvider mep)
            {
                return mep.ValueExpression;
            }
            return otlpHeaders as string;
        }

        private string BuildJson(string? primaryBaseUrl, string? httpBaseUrl, string? httpsBaseUrl, string? resolvedHeaders)
        {
            var pathBase = prefix != null ? $"/{prefix}" : "";
            var environment = new Dictionary<string, string>();
            var normalizedPrimaryBaseUrl = NormalizeUrl(primaryBaseUrl);
            var normalizedHttpBaseUrl = NormalizeUrl(httpBaseUrl ?? (primaryEndpoint.IsHttp ? normalizedPrimaryBaseUrl : null));
            var normalizedHttpsBaseUrl = NormalizeUrl(httpsBaseUrl ?? (primaryEndpoint.IsHttps ? normalizedPrimaryBaseUrl : null));

            foreach (var svc in serviceNames)
            {
                if (normalizedHttpsBaseUrl is not null)
                {
                    environment[$"services__{svc}__https__0"] = $"{normalizedHttpsBaseUrl}{pathBase}/{apiPrefix}/{svc}";
                }

                if (normalizedHttpBaseUrl is not null)
                {
                    environment[$"services__{svc}__http__0"] = $"{normalizedHttpBaseUrl}{pathBase}/{apiPrefix}/{svc}";
                }
            }

            if (proxyTelemetry)
            {
                environment["OTEL_SERVICE_NAME"] = resourceName;

                var telemetryBaseUrl = normalizedHttpsBaseUrl ?? normalizedHttpBaseUrl ?? normalizedPrimaryBaseUrl;
                if (telemetryBaseUrl is not null)
                {
                    var otlpBase = $"{telemetryBaseUrl}{pathBase}/{otlpPrefix}";
                    environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"{otlpBase}/";
                    environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

                    // Per-signal endpoints so that parameterless AddOtlpExporter() works
                    // without needing UseOtlpExporter (which has WASM compatibility issues).
                    environment["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"] = $"{otlpBase}/v1/metrics";
                    environment["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = $"{otlpBase}/v1/traces";
                    environment["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = $"{otlpBase}/v1/logs";
                }

                if (!string.IsNullOrEmpty(resolvedHeaders))
                {
                    environment["OTEL_EXPORTER_OTLP_HEADERS"] = resolvedHeaders;
                }
            }

            return JsonSerializer.Serialize(
                new ClientConfiguration
                {
                    WebAssembly = new WebAssemblyConfiguration { Environment = environment }
                },
                ManifestJsonContext.Default.ClientConfiguration);
        }

        private static string? NormalizeUrl(string? url)
        {
            return string.IsNullOrEmpty(url) ? null : url.TrimEnd('/');
        }
    }
}
