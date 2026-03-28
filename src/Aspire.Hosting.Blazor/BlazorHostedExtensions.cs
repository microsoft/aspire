// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREATS001 // AspireExportIgnore is experimental

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for configuring a Blazor Web App (hosted model) to proxy
/// service calls and telemetry from its WebAssembly client.
/// </summary>
public static class BlazorHostedExtensions
{
    /// <summary>
    /// Configures the host to proxy requests from the WebAssembly client to the specified service.
    /// The WASM client can reach this service via <c>/_api/{serviceName}/{path}</c>.
    /// YARP routes and clusters are emitted as environment variables.
    /// A <c>/_blazor/_configuration</c> response is built so the WASM client gets the proxy URL.
    /// This is an explicit opt-in — <c>WithReference</c> makes the service available to the server,
    /// while <c>ProxyService</c> additionally makes it available to the WASM client.
    /// </summary>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyService(
        this IResourceBuilder<ProjectResource> host,
        IResourceBuilder<IResourceWithServiceDiscovery> service)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.ServiceNames.Add(service.Resource.Name);

        // Forward the service reference to the host so YARP can resolve it via service discovery.
        var existingRefs = GetReferencedResourceNames(host.Resource);
        if (!existingRefs.Contains(service.Resource.Name))
        {
            host.WithReference(service);
        }

        EnsureEnvironmentCallback(host, annotation);

        return host;
    }

    /// <summary>
    /// Configures the host to proxy OpenTelemetry data from the WebAssembly client to the Aspire dashboard.
    /// The WASM client sends OTLP data to <c>/_otlp/{path}</c> which gets forwarded to the dashboard.
    /// Also sets the <c>OTEL_SERVICE_NAME</c> in the client configuration so telemetry from the
    /// WASM client appears with the correct service name in the dashboard.
    /// </summary>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyTelemetry(
        this IResourceBuilder<ProjectResource> host)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.ProxyTelemetry = true;

        EnsureEnvironmentCallback(host, annotation);

        return host;
    }

    private static void EnsureEnvironmentCallback(
        IResourceBuilder<ProjectResource> host,
        HostedClientAnnotation annotation)
    {
        if (annotation.IsInitialized)
        {
            return;
        }

        annotation.IsInitialized = true;

        host.WithEnvironment(context =>
        {
            // Build the client config JSON response with the service URLs and OTLP endpoint.
            // The hosted app serves at / (no prefix), so URLs are relative to the root.
            var serviceNames = annotation.ServiceNames.ToArray();
            var environment = new Dictionary<string, string>();

            foreach (var svc in serviceNames)
            {
                environment[$"services__{svc}__https__0"] = $"/_api/{svc}";
            }

            if (annotation.ProxyTelemetry)
            {
                environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "/_otlp/";
                environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
                environment["OTEL_SERVICE_NAME"] = host.Resource.Name;
            }

            var configJson = JsonSerializer.Serialize(
                new ClientConfiguration
                {
                    WebAssembly = new WebAssemblyConfiguration { Environment = environment }
                },
                ManifestJsonContext.Default.ClientConfiguration);

            context.EnvironmentVariables["Client__ConfigResponse"] = configJson;
            context.EnvironmentVariables["Client__ConfigEndpointPath"] = "/_blazor/_configuration";

            // YARP routes for service proxying (no path prefix — hosted app at /)
            foreach (var svc in serviceNames)
            {
                var routeId = $"route-{svc}";
                var clusterId = $"cluster-{svc}";

                context.EnvironmentVariables[$"ReverseProxy__Routes__{routeId}__ClusterId"] = clusterId;
                context.EnvironmentVariables[$"ReverseProxy__Routes__{routeId}__Match__Path"] = $"/_api/{svc}/{{**catch-all}}";
                context.EnvironmentVariables[$"ReverseProxy__Routes__{routeId}__Transforms__0__PathRemovePrefix"] = $"/_api/{svc}";
                context.EnvironmentVariables[$"ReverseProxy__Clusters__{clusterId}__Destinations__d1__Address"] = $"https+http://{svc}";
            }

            // YARP routes for OTLP proxying
            if (annotation.ProxyTelemetry)
            {
                context.EnvironmentVariables["ReverseProxy__Routes__route-otlp__ClusterId"] = "cluster-otlp-dashboard";
                context.EnvironmentVariables["ReverseProxy__Routes__route-otlp__Match__Path"] = "/_otlp/{**catch-all}";
                context.EnvironmentVariables["ReverseProxy__Routes__route-otlp__Transforms__0__PathRemovePrefix"] = "/_otlp";

                if (context.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpoint))
                {
                    context.EnvironmentVariables["ReverseProxy__Clusters__cluster-otlp-dashboard__Destinations__d1__Address"] = otlpEndpoint;
                }

                // Forward OTLP auth headers
                if (context.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var headersObj)
                    && headersObj is string headersStr)
                {
                    var transformIndex = 1;
                    foreach (var header in headersStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = header.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            context.EnvironmentVariables[$"ReverseProxy__Routes__route-otlp__Transforms__{transformIndex}__RequestHeader"] = parts[0].Trim();
                            context.EnvironmentVariables[$"ReverseProxy__Routes__route-otlp__Transforms__{transformIndex}__Set"] = parts[1].Trim();
                            transformIndex++;
                        }
                    }
                }
            }
        });
    }

    private static HostedClientAnnotation GetOrAddHostedClientAnnotation(IResource resource)
    {
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is HostedClientAnnotation existing)
            {
                return existing;
            }
        }

        var newAnnotation = new HostedClientAnnotation();
        resource.Annotations.Add(newAnnotation);
        return newAnnotation;
    }

    private static HashSet<string> GetReferencedResourceNames(IResource resource)
    {
        var names = new HashSet<string>();
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is ResourceRelationshipAnnotation rel)
            {
                names.Add(rel.Resource.Name);
            }
        }
        return names;
    }
}

/// <summary>
/// Annotation stored on a host resource that tracks proxied services and telemetry configuration.
/// </summary>
internal class HostedClientAnnotation : IResourceAnnotation
{
    public List<string> ServiceNames { get; } = new();
    public bool ProxyTelemetry { get; set; }
    public bool IsInitialized { get; set; }
}
