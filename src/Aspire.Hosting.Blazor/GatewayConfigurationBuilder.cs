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
        EndpointReference gatewayEndpoint)
    {
        var addedClusters = new HashSet<string>();

        // Capture OTLP headers so the WASM client can send them directly
        env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var otlpHeaders);

        foreach (var reg in apps)
        {
            var prefix = reg.PathPrefix;
            var envPrefix = $"ClientApps__{reg.Resource.Name}";

            // Per-app client config: use an IValueProvider that resolves the gateway URL
            // at startup and builds the final JSON response.
            env[$"{envPrefix}__ConfigResponse"] = new ClientConfigValueProvider(
                gatewayEndpoint, prefix, reg.Resource.Name, reg.ServiceNames, proxyTelemetry: true, otlpHeaders);

            EmitYarpRoutes(env, prefix, reg.Resource.Name, reg.ServiceNames, proxyTelemetry: true, addedClusters);
        }

        EmitOtlpCluster(env);
    }

    /// <summary>
    /// Emits YARP route/cluster and client configuration for a hosted Blazor app
    /// (no path prefix, telemetry optional).
    /// </summary>
    public static void EmitHostedProxyConfiguration(
        IDictionary<string, object> env,
        EndpointReference hostEndpoint,
        string resourceName,
        string[] serviceNames,
        bool proxyTelemetry,
        string? httpOtlpEndpointUrl)
    {
        // Capture OTLP headers so the WASM client can send them directly
        env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var otlpHeaders);

        env["Client__ConfigResponse"] = new ClientConfigValueProvider(
            hostEndpoint, prefix: null, resourceName, serviceNames, proxyTelemetry, otlpHeaders);
        env["Client__ConfigEndpointPath"] = "/_blazor/_configuration";

        EmitYarpRoutes(env, prefix: null, resourceName, serviceNames, proxyTelemetry, addedClusters: null);

        if (proxyTelemetry)
        {
            EmitOtlpCluster(env, httpOtlpEndpointUrl);
        }
    }

    /// <summary>
    /// Emits YARP route and cluster entries for service proxying and (optionally) OTLP proxying.
    /// When <paramref name="prefix"/> is null, routes are emitted without a path prefix (hosted model).
    /// When <paramref name="addedClusters"/> is provided, clusters are deduplicated across apps.
    /// </summary>
    private static void EmitYarpRoutes(
        IDictionary<string, object> env,
        string? prefix,
        string resourceName,
        string[] serviceNames,
        bool proxyTelemetry,
        HashSet<string>? addedClusters)
    {
        var pathBase = prefix != null ? $"/{prefix}" : "";

        foreach (var svc in serviceNames)
        {
            var routeId = prefix != null ? $"route-{resourceName}-{svc}" : $"route-{svc}";
            var clusterId = $"cluster-{svc}";

            env[$"ReverseProxy__Routes__{routeId}__ClusterId"] = clusterId;
            env[$"ReverseProxy__Routes__{routeId}__Match__Path"] = $"{pathBase}/_api/{svc}/{{**catch-all}}";
            env[$"ReverseProxy__Routes__{routeId}__Transforms__0__PathRemovePrefix"] = $"{pathBase}/_api/{svc}";

            if (addedClusters == null || addedClusters.Add(clusterId))
            {
                env[$"ReverseProxy__Clusters__{clusterId}__Destinations__d1__Address"] = $"https+http://{svc}";
            }
        }

        if (proxyTelemetry)
        {
            var otlpRouteId = prefix != null ? $"route-otlp-{resourceName}" : "route-otlp";
            env[$"ReverseProxy__Routes__{otlpRouteId}__ClusterId"] = "cluster-otlp-dashboard";
            env[$"ReverseProxy__Routes__{otlpRouteId}__Match__Path"] = $"{pathBase}/_otlp/{{**catch-all}}";
            env[$"ReverseProxy__Routes__{otlpRouteId}__Transforms__0__PathRemovePrefix"] = $"{pathBase}/_otlp";

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
        EndpointReference endpoint,
        string? prefix,
        string resourceName,
        string[] serviceNames,
        bool proxyTelemetry,
        object? otlpHeaders) : IValueProvider, IManifestExpressionProvider
    {
        string IManifestExpressionProvider.ValueExpression =>
            BuildJson(((IManifestExpressionProvider)endpoint).ValueExpression, ResolveHeadersExpression());

        async ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
        {
            var url = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
            var headers = await ResolveHeadersAsync(cancellationToken).ConfigureAwait(false);
            return BuildJson(url!.TrimEnd('/'), headers);
        }

        async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
        {
            var url = await endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
            var headers = await ResolveHeadersAsync(cancellationToken).ConfigureAwait(false);
            return BuildJson(url!.TrimEnd('/'), headers);
        }

        private async ValueTask<string?> ResolveHeadersAsync(CancellationToken cancellationToken)
        {
            if (otlpHeaders is IValueProvider vp)
            {
                return await vp.GetValueAsync(cancellationToken).ConfigureAwait(false);
            }
            return otlpHeaders as string;
        }

        private string? ResolveHeadersExpression()
        {
            if (otlpHeaders is IManifestExpressionProvider mep)
            {
                return mep.ValueExpression;
            }
            return otlpHeaders as string;
        }

        private string BuildJson(string baseUrl, string? resolvedHeaders)
        {
            var pathBase = prefix != null ? $"/{prefix}" : "";
            var environment = new Dictionary<string, string>();
            foreach (var svc in serviceNames)
            {
                environment[$"services__{svc}__https__0"] = $"{baseUrl}{pathBase}/_api/{svc}";
            }
            if (proxyTelemetry)
            {
                environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"{baseUrl}{pathBase}/_otlp/";
                environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
                environment["OTEL_SERVICE_NAME"] = resourceName;
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
    }
}
