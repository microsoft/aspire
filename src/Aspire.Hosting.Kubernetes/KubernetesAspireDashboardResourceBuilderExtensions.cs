// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for creating Aspire Dashboard resources in the application model.
/// </summary>
public static class KubernetesAspireDashboardResourceBuilderExtensions
{
    /// <summary>
    /// Creates a new Aspire Dashboard resource builder with the specified name.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> instance.</param>
    /// <param name="name">The name of the Aspire Dashboard resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/>.</returns>
    /// <remarks>
    /// This method initializes a new Aspire Dashboard resource with HTTP (port 18888),
    /// OTLP gRPC (port 18889), and OTLP HTTP (port 18890) endpoints. The dashboard is
    /// configured for unsecured cluster-internal access by default so it can be used behind
    /// an ingress controller or other Kubernetes networking layer.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is <c>null</c> or empty.</exception>
    internal static IResourceBuilder<KubernetesAspireDashboardResource> CreateDashboard(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new KubernetesAspireDashboardResource(name);

        return builder.CreateResourceBuilder(resource)
                      .WithImage("mcr.microsoft.com/dotnet/nightly/aspire-dashboard")
                      .WithHttpEndpoint(targetPort: 18888)
                      // Expose the HTTP endpoint so ingress or explicit host port mapping can route browser traffic to the dashboard.
                      .WithEndpoint("http", e => e.IsExternal = true)
                      .WithHttpEndpoint(name: "otlp-grpc", targetPort: 18889)
                      .WithHttpEndpoint(name: "otlp-http", targetPort: 18890);
    }

    /// <summary>
    /// Sets the Kubernetes Service port for the Aspire Dashboard HTTP endpoint.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance to configure.</param>
    /// <param name="port">The Service port number. Cluster-internal clients will connect to this port,
    /// which routes to the dashboard's container port (18888). If <c>null</c>, the Service port
    /// defaults to the container port.</param>
    /// <returns>The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance for chaining.</returns>
    /// <remarks>
    /// This sets the <c>port</c> field on the generated Kubernetes Service while keeping the
    /// <c>targetPort</c> as the original container port. This is useful when placing the dashboard
    /// behind an ingress controller that expects a specific Service port (for example, port 80).
    /// To access the dashboard from outside the cluster, use <c>kubectl port-forward</c> or
    /// configure the environment's <see cref="KubernetesEnvironmentResource.DefaultServiceType"/>
    /// to <c>NodePort</c> or <c>LoadBalancer</c>.
    /// </remarks>
    [AspireExport(Description = "Sets the Kubernetes Service port for the Aspire dashboard")]
    public static IResourceBuilder<KubernetesAspireDashboardResource> WithServicePort(
        this IResourceBuilder<KubernetesAspireDashboardResource> builder,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", e =>
        {
            e.Port = port;
            e.IsExternal = port is not null;
        });
    }

    /// <summary>
    /// Sets the Kubernetes Service ports for the Aspire Dashboard OTLP endpoints.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance to configure.</param>
    /// <param name="grpcPort">The Service port for the OTLP gRPC endpoint. If <c>null</c>, the Service port
    /// defaults to the container port (18889).</param>
    /// <param name="httpPort">The Service port for the OTLP HTTP endpoint. If <c>null</c>, the Service port
    /// defaults to the container port (18890).</param>
    /// <returns>The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance for chaining.</returns>
    /// <remarks>
    /// This sets the <c>port</c> field on the generated Kubernetes Service for the OTLP endpoints
    /// while keeping the <c>targetPort</c> as the original container port. Application resources
    /// in the cluster send telemetry to these Service ports. Use standard OTLP ports (4317 for gRPC,
    /// 4318 for HTTP) if your services are configured with those defaults.
    /// </remarks>
    [AspireExport(Description = "Sets the Kubernetes Service ports for the OTLP endpoints")]
    public static IResourceBuilder<KubernetesAspireDashboardResource> WithOtlpServicePort(
        this IResourceBuilder<KubernetesAspireDashboardResource> builder,
        int? grpcPort = null,
        int? httpPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (grpcPort is not null)
        {
            builder = builder.WithEndpoint("otlp-grpc", e => e.Port = grpcPort);
        }

        if (httpPort is not null)
        {
            builder = builder.WithEndpoint("otlp-http", e => e.Port = httpPort);
        }

        return builder;
    }

    /// <summary>
    /// Configures whether forwarded headers processing is enabled for the Aspire dashboard container.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance.</param>
    /// <param name="enabled">True to enable forwarded headers, false to disable.</param>
    /// <returns>The same <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> to allow chaining.</returns>
    /// <remarks>
    /// This sets the <c>ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED</c> environment variable inside the dashboard
    /// container. When enabled, the dashboard will process <c>X-Forwarded-Host</c> and <c>X-Forwarded-Proto</c>
    /// headers which is required when the dashboard is accessed through a reverse proxy or ingress controller.
    /// </remarks>
    [AspireExport(Description = "Enables or disables forwarded headers support for the Aspire dashboard")]
    public static IResourceBuilder<KubernetesAspireDashboardResource> WithForwardedHeaders(
        this IResourceBuilder<KubernetesAspireDashboardResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment("ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED", enabled ? "true" : "false");
    }
}
