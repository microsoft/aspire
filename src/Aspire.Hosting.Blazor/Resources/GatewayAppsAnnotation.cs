// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Annotation stored on a gateway resource that tracks all registered Blazor WASM app registrations.
/// Replaces the previous static Dictionary approach, keeping state on the resource itself.
/// </summary>
internal sealed class GatewayAppsAnnotation : IResourceAnnotation
{
    public List<GatewayAppRegistration> Apps { get; } = [];
    public bool IsInitialized { get; set; }
}

internal record GatewayAppRegistration(
    IResourceBuilder<BlazorWasmAppResource> AppBuilder,
    string PathPrefix,
    GatewayAppService[] Services,
    string ApiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
    string OtlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
    bool ProxyBlazorTelemetry = true)
{
    public BlazorWasmAppResource Resource => AppBuilder.Resource;

    /// <summary>
    /// Gets the service names from the registered services.
    /// </summary>
    public string[] GetServiceNames() => Services.Select(s => s.Name).ToArray();
}

/// <summary>
/// Represents a backend service that a Blazor WASM app communicates with through the gateway.
/// Carries the service name and optional endpoint names for service discovery routing.
/// </summary>
internal sealed class GatewayAppService(string name)
{
    /// <summary>
    /// The resource name of the service (e.g., "weatherapi").
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Specific endpoint names referenced on this service, or empty if all endpoints are used.
    /// When non-empty, YARP uses the first endpoint name with .NET service discovery's
    /// named endpoint format (e.g., <c>https+http://_api.weatherapi</c>).
    /// </summary>
    public List<string> EndpointNames { get; } = [];

    /// <summary>
    /// Whether all endpoints should be used (scheme-based resolution).
    /// </summary>
    public bool UseAllEndpoints => EndpointNames.Count == 0;
}
