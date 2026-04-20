// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kubernetes environment resources to the application model.
/// </summary>
public static class KubernetesEnvironmentExtensions
{
    internal static IDistributedApplicationBuilder AddKubernetesInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddEventingSubscriber<KubernetesInfrastructure>();
        builder.Services.TryAddSingleton<IHelmRunner, DefaultHelmRunner>();

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
            HelmChartName = builder.Environment.ApplicationName.ToHelmChartName(),
            Dashboard = builder.CreateDashboard($"{name}-dashboard")
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
    /// </remarks>
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
    [AspireExport(Description = "Configures Helm chart deployment settings", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<KubernetesEnvironmentResource> WithHelm(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        Action<HelmChartOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Set the Helm deployment engine
        builder.Resource.DeploymentEngineStepsFactory = HelmDeploymentEngine.CreateStepsAsync;

        if (configure is not null)
        {
            var options = new HelmChartOptions(builder);
            configure(options);
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

    /// <summary>
    /// Enables the Aspire dashboard for telemetry visualization in this Kubernetes environment.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="enabled">Whether to enable the dashboard. Default is true.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// When enabled, an Aspire Dashboard container is deployed alongside the application resources
    /// in the Kubernetes cluster. All resources with OTLP telemetry support are automatically
    /// configured to send telemetry data to the dashboard.
    /// </remarks>
    [AspireExport(Description = "Enables or disables the Aspire dashboard for the Kubernetes environment")]
    public static IResourceBuilder<KubernetesEnvironmentResource> WithDashboard(this IResourceBuilder<KubernetesEnvironmentResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.DashboardEnabled = enabled;

        return builder;
    }

    /// <summary>
    /// Configures the dashboard properties for this Kubernetes environment.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the dashboard resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Use this overload to customize the dashboard container, for example to set a specific host port
    /// or enable forwarded headers for ingress access.
    /// </remarks>
    [AspireExport("configureDashboard", MethodName = "configureDashboard", Description = "Configures the Aspire dashboard resource for the Kubernetes environment", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<KubernetesEnvironmentResource> WithDashboard(this IResourceBuilder<KubernetesEnvironmentResource> builder, Action<IResourceBuilder<KubernetesAspireDashboardResource>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Resource.DashboardEnabled = true;

        configure(builder.Resource.Dashboard ?? throw new InvalidOperationException("Dashboard resource is not initialized"));

        return builder;
    }

    /// <summary>
    /// Adds a named node pool to the Kubernetes environment.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the node pool. This value is used as the <c>nodeSelector</c> value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesNodePoolResource}"/> for the new node pool.</returns>
    /// <remarks>
    /// For vanilla Kubernetes, this creates a named reference to an existing node pool.
    /// For managed Kubernetes services (e.g., AKS), the cloud-specific <c>AddNodePool</c> overload
    /// provisions the pool with additional configuration such as VM size and autoscaling.
    /// Use <see cref="WithNodePool{T}"/> to schedule workloads on the returned node pool.
    /// </remarks>
    /// <example>
    /// <code>
    /// var k8s = builder.AddKubernetesEnvironment("k8s");
    /// var gpuPool = k8s.AddNodePool("gpu");
    ///
    /// builder.AddProject&lt;MyApi&gt;()
    ///     .WithComputeEnvironment(k8s)
    ///     .WithNodePool(gpuPool);
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a named node pool to a Kubernetes environment")]
    public static IResourceBuilder<KubernetesNodePoolResource> AddNodePool(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var nodePool = new KubernetesNodePoolResource(name, builder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(nodePool);
        }

        return builder.ApplicationBuilder.AddResource(nodePool)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Schedules a compute resource's workload on the specified Kubernetes node pool.
    /// This translates to a Kubernetes <c>nodeSelector</c> in the pod specification
    /// targeting the named node pool.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nodePool">The node pool to schedule the workload on.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var k8s = builder.AddKubernetesEnvironment("k8s");
    /// var gpuPool = k8s.AddNodePool("gpu");
    ///
    /// builder.AddProject&lt;MyApi&gt;()
    ///     .WithComputeEnvironment(k8s)
    ///     .WithNodePool(gpuPool);
    /// </code>
    /// </example>
    [AspireExport("withKubernetesNodePool", Description = "Schedules a workload on a specific Kubernetes node pool")]
    public static IResourceBuilder<T> WithNodePool<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesNodePoolResource> nodePool)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(nodePool);

        builder.WithAnnotation(new KubernetesNodePoolAnnotation(nodePool.Resource));
        return builder;
    }

    internal static void EnsureDefaultHelmEngine(IResourceBuilder<KubernetesEnvironmentResource> builder)
    {
        builder.Resource.DeploymentEngineStepsFactory ??= HelmDeploymentEngine.CreateStepsAsync;
    }
}
