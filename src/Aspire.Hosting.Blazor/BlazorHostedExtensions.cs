// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    /// The WASM client can reach this service via <c>/{apiPrefix}/{serviceName}/{path}</c>.
    /// YARP routes and clusters are emitted as environment variables.
    /// A <c>/_blazor/_configuration</c> response is built so the WASM client gets the proxy URL.
    /// This is an explicit opt-in — <c>WithReference</c> makes the service available to the server,
    /// while <c>ProxyService</c> additionally makes it available to the WASM client.
    /// </summary>
    /// <param name="host">The host resource builder.</param>
    /// <param name="service">The service to proxy.</param>
    /// <param name="apiPrefix">The URL path prefix for API proxy routes. Defaults to <c>"_api"</c>.</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyService(
        this IResourceBuilder<ProjectResource> host,
        IResourceBuilder<IResourceWithServiceDiscovery> service,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.ServiceNames.Add(service.Resource.Name);
        annotation.ApiPrefix = apiPrefix;

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
    /// The WASM client sends OTLP data to <c>/{otlpPrefix}/{path}</c> which gets forwarded to the dashboard.
    /// Also sets the <c>OTEL_SERVICE_NAME</c> in the client configuration so telemetry from the
    /// WASM client appears with the correct service name in the dashboard.
    /// </summary>
    /// <param name="host">The host resource builder.</param>
    /// <param name="otlpPrefix">The URL path prefix for OTLP proxy routes. Defaults to <c>"_otlp"</c>.</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyTelemetry(
        this IResourceBuilder<ProjectResource> host,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.ProxyTelemetry = true;
        annotation.OtlpPrefix = otlpPrefix;

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
            var httpsHostEndpoint = GetEndpointIfDefined(host.Resource, "https");
            var httpHostEndpoint = GetEndpointIfDefined(host.Resource, "http");
            var hostEndpoint = httpsHostEndpoint ?? httpHostEndpoint
                ?? throw new InvalidOperationException($"The host '{host.Resource.Name}' must define an HTTP or HTTPS endpoint.");

            // Resolve the HTTP OTLP endpoint for WASM client proxying.
            // WASM clients use HTTP/protobuf (not gRPC), so we need the HTTP endpoint.
            var httpOtlpEndpointUrl = host.ApplicationBuilder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]
                ?? host.ApplicationBuilder.Configuration["DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"];

            GatewayConfigurationBuilder.EmitHostedProxyConfiguration(
                context.EnvironmentVariables,
                hostEndpoint,
                httpHostEndpoint,
                $"{host.Resource.Name} (client)",
                annotation.ServiceNames.ToArray(),
                annotation.ProxyTelemetry,
                httpOtlpEndpointUrl,
                annotation.ApiPrefix,
                annotation.OtlpPrefix);
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

    private static EndpointReference? GetEndpointIfDefined(IResourceWithEndpoints resource, string endpointName)
    {
        var endpoint = resource.GetEndpoint(endpointName);
        return endpoint.Exists ? endpoint : null;
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
    public string ApiPrefix { get; set; } = GatewayConfigurationBuilder.DefaultApiPrefix;
    public string OtlpPrefix { get; set; } = GatewayConfigurationBuilder.DefaultOtlpPrefix;
}
