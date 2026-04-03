// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for creating Aspire Dashboard resources in the application model.
/// </summary>
public static class DockerComposeAspireDashboardResourceBuilderExtensions
{
    /// <summary>
    /// Creates a new Aspire Dashboard resource builder with the specified name.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> instance.</param>
    /// <param name="name">The name of the Aspire Dashboard resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AspireDashboardResource}"/>.</returns>
    /// <remarks>
    /// This method initializes a new Aspire Dashboard resource that can be used to visualize telemetry data
    /// in the Aspire Hosting environment. The resource is not automatically added to the application model;
    /// instead, it returns a builder that can be further configured and added as needed.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is <c>null</c> or empty.</exception>
    internal static IResourceBuilder<DockerComposeAspireDashboardResource> CreateDashboard(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new DockerComposeAspireDashboardResource(name);

        // Initialize the dashboard resource
        return builder.CreateResourceBuilder(resource)
                      .WithImage("mcr.microsoft.com/dotnet/nightly/aspire-dashboard")
                      .WithHttpEndpoint(targetPort: 18888)
                      // Expose the HTTP endpoint externally for the dashboard, it is password protected
                      // and disabled by default so an explicit call is required to turn it on.
                      .WithEndpoint("http", e => e.IsExternal = true)
                      .WithHttpEndpoint(name: "otlp-grpc", targetPort: 18889)
                      .WithHttpEndpoint(name: "otlp-http", targetPort: 18890);
    }

    /// <summary>
    /// Configures the port used to access the Aspire Dashboard from a browser.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{DockerComposeAspireDashboardResource}"/> instance to configure.</param>
    /// <param name="port">The port to bind on the host. If non-null, the dashboard will be exposed on the host. If <c>null</c>, the dashboard will not be exposed on the host but will only be reachable within the container network.</param>
    /// <returns>
    /// The <see cref="IResourceBuilder{DockerComposeAspireDashboardResource}"/> instance for chaining.
    /// </returns>
    [AspireExport(Description = "Sets the host port for the Aspire dashboard")]
    public static IResourceBuilder<DockerComposeAspireDashboardResource> WithHostPort(
        this IResourceBuilder<DockerComposeAspireDashboardResource> builder,
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
    /// <param name="builder">The <see cref="IResourceBuilder{DockerComposeAspireDashboardResource}"/> instance.</param>
    /// <param name="enabled">True to enable forwarded headers (<c>ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED=true</c>), false to disable it (sets the value to <c>false</c>).</param>
    /// <returns>The same <see cref="IResourceBuilder{DockerComposeAspireDashboardResource}"/> to allow chaining.</returns>
    /// <remarks>
    /// This sets the <c>ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED</c> environment variable inside the dashboard
    /// container. When enabled, the dashboard will process <c>X-Forwarded-Host</c> and <c>X-Forwarded-Proto</c>
    /// headers which is required when the dashboard is accessed through a reverse proxy or load balancer.
    /// </remarks>
    [AspireExport(Description = "Enables or disables forwarded headers support for the Aspire dashboard")]
    public static IResourceBuilder<DockerComposeAspireDashboardResource> WithForwardedHeaders(
        this IResourceBuilder<DockerComposeAspireDashboardResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment("ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED", enabled ? "true" : "false");
    }
}
