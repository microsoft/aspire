// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kubernetes environment resources to the application model.
/// </summary>
public static class KubernetesEnvironmentExtensions
{
    internal static IDistributedApplicationBuilder AddKubernetesInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddEventingSubscriber<KubernetesInfrastructure>();

        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the Kubernetes environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesEnvironmentResource}"/>.</returns>
    [AspireExport(Description = "Adds a Kubernetes publishing environment")]
    public static IResourceBuilder<KubernetesEnvironmentResource> AddKubernetesEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddKubernetesInfrastructureCore();

        var resource = new KubernetesEnvironmentResource(name)
        {
            HelmChartName = builder.Environment.ApplicationName.ToHelmChartName()
        };
        if (builder.ExecutionContext.IsRunMode)
        {

            // Return a builder that isn't added to the top-level application builder
            // so it doesn't surface as a resource.
            return builder.CreateResourceBuilder(resource);

        }

        var resourceBuilder = builder.AddResource(resource);

        // Default to Helm deployment engine if not already configured
        EnsureDefaultHelmEngine(resourceBuilder);

        return resourceBuilder;
    }

    /// <summary>
    /// Configures the Kubernetes environment to deploy using Helm charts.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="configure">An optional callback to configure Helm chart settings such as namespace, release name, and chart version.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Helm is the default deployment engine. Call this method to customize Helm-specific settings.
    /// <example>
    /// Configure Helm deployment with custom settings:
    /// <code>
    /// builder.AddKubernetesEnvironment("k8s")
    ///     .WithHelm(helm =>
    ///     {
    ///         helm.WithNamespace("my-namespace");
    ///         helm.WithReleaseName("my-release");
    ///         helm.WithChartVersion("1.0.0");
    ///     });
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("withHelm", Description = "Configures Helm chart deployment settings", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<KubernetesEnvironmentResource> WithHelm(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        Action<HelmChartConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Add or replace the Helm deployment engine annotation
        builder.WithAnnotation(
            new KubernetesDeploymentEngineAnnotation(HelmDeploymentEngine.CreateStepsAsync),
            ResourceAnnotationMutationBehavior.Replace);

        if (configure is not null)
        {
            var configuration = new HelmChartConfiguration(builder);
            configure(configuration);
        }

        return builder;
    }

    /// <summary>
    /// Allows setting the properties of a Kubernetes environment resource.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="KubernetesEnvironmentResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures properties of a Kubernetes environment", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<KubernetesEnvironmentResource> WithProperties(this IResourceBuilder<KubernetesEnvironmentResource> builder, Action<KubernetesEnvironmentResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    private static void EnsureDefaultHelmEngine(IResourceBuilder<KubernetesEnvironmentResource> builder)
    {
        if (!builder.Resource.HasAnnotationOfType<KubernetesDeploymentEngineAnnotation>())
        {
            builder.WithAnnotation(new KubernetesDeploymentEngineAnnotation(HelmDeploymentEngine.CreateStepsAsync));
        }
    }
}
