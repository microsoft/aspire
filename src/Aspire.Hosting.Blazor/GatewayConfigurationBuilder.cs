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

        foreach (var reg in apps)
        {
            var prefix = reg.PathPrefix;
            var envPrefix = $"ClientApps__{reg.Resource.Name}";

            // Per-app client config: use an IValueProvider that resolves the gateway URL
            // at startup and builds the final JSON response.
            env[$"{envPrefix}__ConfigResponse"] = new ClientConfigValueProvider(
                gatewayEndpoint, prefix, reg.Resource.Name, reg.ServiceNames);

            // YARP routes: per-app API proxy (route ID is the section key, not a field)
            foreach (var svc in reg.ServiceNames)
            {
                var routeId = $"route-{reg.Resource.Name}-{svc}";
                var clusterId = $"cluster-{svc}";

                env[$"ReverseProxy__Routes__{routeId}__ClusterId"] = clusterId;
                env[$"ReverseProxy__Routes__{routeId}__Match__Path"] = $"/{prefix}/_api/{svc}/{{**catch-all}}";
                env[$"ReverseProxy__Routes__{routeId}__Transforms__0__PathRemovePrefix"] = $"/{prefix}/_api/{svc}";

                // YARP cluster (add once, shared across apps referencing the same service)
                if (addedClusters.Add(clusterId))
                {
                    env[$"ReverseProxy__Clusters__{clusterId}__Destinations__d1__Address"] = $"https+http://{svc}";
                }
            }

            // YARP route: per-app OTLP proxy
            var otlpRouteId = $"route-otlp-{reg.Resource.Name}";
            env[$"ReverseProxy__Routes__{otlpRouteId}__ClusterId"] = "cluster-otlp-dashboard";
            env[$"ReverseProxy__Routes__{otlpRouteId}__Match__Path"] = $"/{prefix}/_otlp/{{**catch-all}}";
            env[$"ReverseProxy__Routes__{otlpRouteId}__Transforms__0__PathRemovePrefix"] = $"/{prefix}/_otlp";

            // Inject OTLP auth headers as YARP request header transforms
            // (OTEL_EXPORTER_OTLP_HEADERS is already in the env dict from Aspire's OTLP configuration callback)
            if (env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var headersObj) && headersObj is string headersStr)
            {
                var transformIndex = 1; // 0 is PathRemovePrefix
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

        // YARP cluster: shared OTLP dashboard — forward from OTEL_EXPORTER_OTLP_ENDPOINT.
        // Aspire stores this as a HostUrl (IValueProvider), not a string. Forward the object
        // directly so Aspire resolves it to the actual URL when starting the process.
        if (env.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpoint))
        {
            env["ReverseProxy__Clusters__cluster-otlp-dashboard__Destinations__d1__Address"] = otlpEndpoint;
        }
    }

    /// <summary>
    /// An IValueProvider that resolves the gateway's endpoint URL and builds the
    /// Blazor WASM configuration JSON response. At run time, the gateway URL is
    /// resolved from the EndpointReference. At publish time, ValueExpression emits
    /// the JSON with manifest expression placeholders for the deployer to resolve.
    /// </summary>
    private sealed class ClientConfigValueProvider(
        EndpointReference gatewayEndpoint,
        string prefix,
        string resourceName,
        string[] serviceNames) : IValueProvider, IManifestExpressionProvider
    {
        string IManifestExpressionProvider.ValueExpression =>
            BuildJson(((IManifestExpressionProvider)gatewayEndpoint).ValueExpression);

        async ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
        {
            var url = await gatewayEndpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
            return BuildJson(url!.TrimEnd('/'));
        }

        async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
        {
            var url = await gatewayEndpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
            return BuildJson(url!.TrimEnd('/'));
        }

        private string BuildJson(string gatewayUrl)
        {
            var environment = new Dictionary<string, string>();
            foreach (var svc in serviceNames)
            {
                environment[$"services__{svc}__https__0"] = $"{gatewayUrl}/{prefix}/_api/{svc}";
            }
            environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"{gatewayUrl}/{prefix}/_otlp/";
            environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
            environment["OTEL_SERVICE_NAME"] = resourceName;

            var config = new ClientConfiguration
            {
                WebAssembly = new WebAssemblyConfiguration
                {
                    Environment = environment
                }
            };
            return JsonSerializer.Serialize(config, ManifestJsonContext.Default.ClientConfiguration);
        }
    }
}
