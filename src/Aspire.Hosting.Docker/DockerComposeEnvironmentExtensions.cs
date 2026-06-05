// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Docker Compose environment resources to the application model.
/// </summary>
public static class DockerComposeEnvironmentExtensions
{
    internal static IDistributedApplicationBuilder AddDockerComposeInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddEventingSubscriber<DockerComposeInfrastructure>();

        return builder;
    }

    /// <summary>
    /// Adds a Docker Compose environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the Docker Compose environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{DockerComposeEnvironmentResource}"/>.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> AddDockerComposeEnvironment(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddDockerComposeInfrastructureCore();

        var resource = new DockerComposeEnvironmentResource(name)
        {
            // Initialize the dashboard resource
            Dashboard = builder.CreateDashboard($"{name}-dashboard")
                               .PublishAsDockerComposeService((_, service) =>
                               {
                                   service.Restart = "always";
                               })
        };

        if (builder.ExecutionContext.IsRunMode)
        {
            // Return a builder that isn't added to the top-level application builder
            // so it doesn't surface as a resource.
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Allows setting the properties of a Docker Compose environment resource.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="DockerComposeEnvironmentResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithProperties(this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<DockerComposeEnvironmentResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Configures the Docker Compose file for the environment resource.
    /// </summary>
    /// <param name="builder"> The Docker compose environment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="ComposeFile"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> ConfigureComposeFile(this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<ComposeFile> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Resource.ConfigureComposeFile += configure;
        return builder;
    }

    /// <summary>
    /// Configures the captured environment variables for the Docker Compose environment before they are written to the .env file.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the captured environment variables.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This callback is invoked during the prepare phase, allowing programmatic modification of the environment variables
    /// that will be written to the .env file adjacent to the Docker Compose file.
    /// </remarks>
    public static IResourceBuilder<DockerComposeEnvironmentResource> ConfigureEnvFile(this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<IDictionary<string, CapturedEnvironmentVariable>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Resource.ConfigureEnvFile += configure;
        return builder;
    }

    /// <summary>
    /// Enables the Aspire dashboard for telemetry visualization in this Docker Compose environment.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="enabled">Whether to enable the dashboard. Default is true.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithDashboard(this IResourceBuilder<DockerComposeEnvironmentResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.DashboardEnabled = enabled;

        return builder;
    }

    /// <summary>
    /// Configures the dashboard properties for this Docker Compose environment.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the dashboard service.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithDashboard(this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<IResourceBuilder<DockerComposeAspireDashboardResource>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // Ensure the dashboard resource is initialized
        builder.Resource.DashboardEnabled = true;

        configure(builder.Resource.Dashboard ?? throw new InvalidOperationException("Dashboard resource is not initialized"));

        return builder;
    }
}
