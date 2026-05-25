// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        object? httpOtlpEndpoint = null,
        ResourceLoggerService? resourceLoggerService = null)
    {
        var addedClusters = new HashSet<string>();
        var httpClientEndpoint = httpGatewayEndpoint ?? (gatewayEndpoint.IsHttp ? gatewayEndpoint : null);
        var httpsClientEndpoint = gatewayEndpoint.IsHttps ? gatewayEndpoint : null;

        foreach (var reg in apps)
        {
            var prefix = reg.PathPrefix;
            var envPrefix = $"ClientApps__{reg.Resource.Name}";

            // Per-app client config: use an IValueProvider that resolves the gateway URL
            // at startup and builds the final JSON response.
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

            env[$"{envPrefix}__ConfigResponse"] = new ClientConfigValueProvider(
                gatewayEndpoint,
                httpClientEndpoint,
                httpsClientEndpoint,
                prefix,
                reg.Resource.Name,
                services,
                reg.ProxyBlazorTelemetry,
                httpOtlpEndpoint,
                resourceLoggerService?.GetLogger(reg.Resource) ?? NullLogger.Instance,
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
        ILogger? logger = null,
        string otlpPrefix = DefaultOtlpPrefix)
    {
        var httpClientEndpoint = httpHostEndpoint ?? (hostEndpoint.IsHttp ? hostEndpoint : null);
        var httpsClientEndpoint = hostEndpoint.IsHttps ? hostEndpoint : null;

        env["Client__ConfigResponse"] = new ClientConfigValueProvider(
            hostEndpoint,
            httpClientEndpoint,
            httpsClientEndpoint,
            prefix: null,
            resourceName,
            services,
            proxyBlazorTelemetry,
            httpOtlpEndpoint,
            logger ?? NullLogger.Instance,
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
        IReadOnlyList<HostedClientService> services,
        bool proxyBlazorTelemetry,
        object? httpOtlpEndpoint,
        ILogger logger,
        string otlpPrefix = DefaultOtlpPrefix) : IValueProvider, IManifestExpressionProvider
    {
        string IManifestExpressionProvider.ValueExpression =>
            BuildJson(
                ((IManifestExpressionProvider)primaryEndpoint).ValueExpression,
                ResolveEndpointExpression(httpEndpoint),
                ResolveEndpointExpression(httpsEndpoint));

        async ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
        {
            LogOtlpWarningIfNeeded();
            var primaryUrl = await primaryEndpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
            var httpUrl = await ResolveEndpointAsync(httpEndpoint, cancellationToken).ConfigureAwait(false);
            var httpsUrl = await ResolveEndpointAsync(httpsEndpoint, cancellationToken).ConfigureAwait(false);
            return BuildJson(primaryUrl, httpUrl, httpsUrl);
        }

        async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
        {
            LogOtlpWarningIfNeeded();
            var primaryUrl = await primaryEndpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
            var httpUrl = await ResolveEndpointAsync(httpEndpoint, context, cancellationToken).ConfigureAwait(false);
            var httpsUrl = await ResolveEndpointAsync(httpsEndpoint, context, cancellationToken).ConfigureAwait(false);
            return BuildJson(primaryUrl, httpUrl, httpsUrl);
        }

        private void LogOtlpWarningIfNeeded()
        {
            if (proxyBlazorTelemetry && httpOtlpEndpoint is null)
            {
                logger.LogWarning(
                    "OTLP telemetry proxying was requested but no dashboard HTTP endpoint could be resolved. " +
                    "WASM client telemetry will not be forwarded.");
            }
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

        private string BuildJson(string? primaryBaseUrl, string? httpBaseUrl, string? httpsBaseUrl)
        {
            var pathBase = prefix != null ? $"/{prefix}" : "";
            var environment = new Dictionary<string, string>();
            var normalizedPrimaryBaseUrl = NormalizeUrl(primaryBaseUrl);
            var normalizedHttpBaseUrl = NormalizeUrl(httpBaseUrl ?? (primaryEndpoint.IsHttp ? normalizedPrimaryBaseUrl : null));
            var normalizedHttpsBaseUrl = NormalizeUrl(httpsBaseUrl ?? (primaryEndpoint.IsHttps ? normalizedPrimaryBaseUrl : null));

            foreach (var svc in services)
            {
                if (normalizedHttpsBaseUrl is not null)
                {
                    environment[$"services__{svc.ServiceName}__https__0"] = $"{normalizedHttpsBaseUrl}{pathBase}/{svc.ApiPrefix}/{svc.ServiceName}";
                }

                if (normalizedHttpBaseUrl is not null)
                {
                    environment[$"services__{svc.ServiceName}__http__0"] = $"{normalizedHttpBaseUrl}{pathBase}/{svc.ApiPrefix}/{svc.ServiceName}";
                }
            }

            if (proxyBlazorTelemetry && httpOtlpEndpoint is not null)
            {
                environment["OTEL_SERVICE_NAME"] = resourceName;

                // Send only the OTLP path so the WASM client resolves it against its own
                // page origin (HostEnvironment.BaseAddress). This avoids cross-origin issues
                // when the user navigates via HTTP but the gateway also exposes HTTPS.
                environment["ASPIRE_OTLP_PATH_BASE"] = $"{pathBase}/{otlpPrefix}";
                environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

                // NOTE: OTEL_EXPORTER_OTLP_HEADERS is intentionally NOT sent to the WASM client.
                // The headers contain the dashboard OTLP API key, and this config is delivered
                // to browser-visible JSON. The YARP proxy injects the headers server-side when
                // forwarding telemetry to the dashboard.
            }

            return JsonSerializer.Serialize(
                new ClientConfiguration
                {
                    WebAssembly = new WebAssemblyConfiguration { Environment = environment }
                },
                ManifestJsonContext.Relaxed.ClientConfiguration);
        }

        private static string? NormalizeUrl(string? url)
        {
            return string.IsNullOrEmpty(url) ? null : url.TrimEnd('/');
        }
    }
}
