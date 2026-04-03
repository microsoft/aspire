// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for creating Aspire Dashboard resources in a Kubernetes environment.
/// </summary>
public static class KubernetesAspireDashboardResourceBuilderExtensions
{
    /// <summary>
    /// Creates a new Aspire Dashboard resource builder for use in a Kubernetes environment.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> instance.</param>
    /// <param name="name">The name of the Aspire Dashboard resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/>.</returns>
    /// <remarks>
    /// This method initializes a new Aspire Dashboard resource with HTTP (port 18888),
    /// OTLP gRPC (port 18889), and OTLP HTTP (port 18890) endpoints. The dashboard is
    /// configured in unsecured mode suitable for cluster-internal access.
    /// </remarks>
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
                      .WithEndpoint("http", e => e.IsExternal = true)
                      .WithHttpEndpoint(name: "otlp-grpc", targetPort: 18889)
                      .WithHttpEndpoint(name: "otlp-http", targetPort: 18890)
                      .WithEnvironment("DASHBOARD__FRONTEND__AUTHMODE", "Unsecured")
                      .WithEnvironment("DASHBOARD__OTLP__AUTHMODE", "Unsecured");
    }

    /// <summary>
    /// Configures the port used to access the Aspire Dashboard from outside the cluster.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance to configure.</param>
    /// <param name="port">The port to expose. If non-null, the dashboard will be exposed externally. If <c>null</c>, the dashboard will only be reachable within the cluster network.</param>
    /// <returns>The <see cref="IResourceBuilder{KubernetesAspireDashboardResource}"/> instance for chaining.</returns>
    [AspireExport(Description = "Sets the host port for the Aspire dashboard")]
    public static IResourceBuilder<KubernetesAspireDashboardResource> WithHostPort(
        this IResourceBuilder<KubernetesAspireDashboardResource> builder,
        int? port = null)
    {
        return builder.WithEndpoint("http", e =>
        {
            e.Port = port;
            e.IsExternal = port is not null;
        });
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
