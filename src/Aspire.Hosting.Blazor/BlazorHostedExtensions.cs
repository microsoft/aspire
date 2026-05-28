// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for configuring a Blazor Web App (hosted model) to proxy
/// service calls and telemetry from its WebAssembly client.
/// </summary>
[Experimental("ASPIREBLAZOR001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class BlazorHostedExtensions
{
    /// <summary>
    /// Configures the host to proxy requests from the WebAssembly client to the specified service.
    /// The WASM client can reach this service via <c>/{apiPrefix}/{serviceName}/{path}</c>.
    /// YARP routes and clusters are emitted as environment variables.
    /// A <c>/_blazor/_configuration</c> response is built so the WASM client gets the proxy URL.
    /// This is an explicit opt-in — <c>WithReference</c> makes the service available to the server,
    /// while <c>ProxyBlazorService</c> additionally makes it available to the WASM client.
    /// </summary>
    /// <param name="host">The host resource builder.</param>
    /// <param name="service">The service to proxy.</param>
    /// <param name="apiPrefix">The URL path prefix for API proxy routes. Defaults to <c>"_api"</c>.</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyBlazorService(
        this IResourceBuilder<ProjectResource> host,
        IResourceBuilder<IResourceWithServiceDiscovery> service,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.Services.Add(new HostedClientService(service.Resource.Name, apiPrefix));

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
    public static IResourceBuilder<ProjectResource> ProxyBlazorTelemetry(
        this IResourceBuilder<ProjectResource> host,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.ProxyBlazorTelemetry = true;
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
            var httpOtlpEndpointUrl = BlazorGatewayExtensions.ResolveHttpOtlpEndpointUrl(context, host.ApplicationBuilder.Configuration);

            if (httpOtlpEndpointUrl is null && annotation.ProxyBlazorTelemetry)
            {
                context.Logger.LogWarning(
                    "OTLP telemetry proxying was requested but no dashboard HTTP endpoint could be resolved. " +
                    "WASM client telemetry will not be forwarded.");
            }

            GatewayConfigurationBuilder.EmitHostedProxyConfiguration(
                context.EnvironmentVariables,
                hostEndpoint,
                httpHostEndpoint,
                $"{host.Resource.Name} (client)",
                annotation.Services,
                annotation.ProxyBlazorTelemetry,
                httpOtlpEndpointUrl,
                annotation.OtlpPrefix);
        });
    }

    private static HostedClientAnnotation GetOrAddHostedClientAnnotation(IResource resource)
    {
        if (resource.TryGetLastAnnotation<HostedClientAnnotation>(out var existing))
        {
            return existing;
        }

        var newAnnotation = new HostedClientAnnotation();
        resource.Annotations.Add(newAnnotation);
        return newAnnotation;
    }

    private static HashSet<string> GetReferencedResourceNames(IResource resource)
    {
        return resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Select(a => a.Resource.Name)
            .ToHashSet(StringComparers.ResourceName);
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
internal sealed class HostedClientAnnotation : IResourceAnnotation
{
    public List<HostedClientService> Services { get; } = [];
    public bool ProxyBlazorTelemetry { get; set; }
    public bool IsInitialized { get; set; }
    public string OtlpPrefix { get; set; } = GatewayConfigurationBuilder.DefaultOtlpPrefix;
}

/// <summary>
/// A service proxied from the hosted Blazor WebAssembly client through the host.
/// </summary>
internal readonly struct HostedClientService(string serviceName, string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix, string? endpointName = null)
{
    public string ServiceName { get; } = serviceName;
    public string ApiPrefix { get; } = apiPrefix;

    /// <summary>
    /// The specific endpoint name to target on the service, or <see langword="null"/> to resolve by scheme.
    /// When set, YARP uses the .NET service discovery named endpoint format
    /// (<c>https+http://_endpointName.serviceName</c>) instead of scheme-based resolution.
    /// </summary>
    public string? EndpointName { get; } = endpointName;

    /// <summary>
    /// Gets the service discovery destination address for YARP.
    /// Uses named endpoint format (<c>_endpointName.serviceName</c>) when a specific endpoint
    /// is targeted; otherwise resolves by scheme.
    /// </summary>
    public string DestinationAddress => EndpointName is not null
        ? $"https+http://_{EndpointName}.{ServiceName}"
        : $"https+http://{ServiceName}";
}
