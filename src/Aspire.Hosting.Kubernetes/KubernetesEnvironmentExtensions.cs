// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Annotations;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kubernetes environment resources to the application model.
/// </summary>
public static class KubernetesEnvironmentExtensions
{
    internal static IDistributedApplicationBuilder AddKubernetesInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<IHelmRunner, DefaultHelmRunner>();

        // Register the pipeline step idempotently. AddKubernetesInfrastructureCore can be
        // called more than once (e.g. when AddKubernetesEnvironment is called for multiple
        // environments). The marker singleton ensures we only add the step the first time.
        //
        // The per-environment work (creating Kubernetes service resources and DeploymentTargetAnnotations)
        // is registered as a separate per-environment pipeline step on KubernetesEnvironmentResource.
        // This global step validates model-wide Kubernetes configuration before those steps filter
        // resources to their selected compute environments.
        if (builder.Services.All(d => d.ServiceType != typeof(KubernetesPipelineStepMarker)))
        {
            builder.Services.AddSingleton<KubernetesPipelineStepMarker>();

            builder.Pipeline.AddStep(
                name: KubernetesPipelineStepMarker.StepName,
                action: ctx =>
                {
                    ValidateAndFinalizePersistentVolumeBindings(ctx);

                    if (!ctx.ExecutionContext.IsPublishMode)
                    {
                        return Task.CompletedTask;
                    }

                    var hasKubernetesEnvironment = ctx.Model.Resources.OfType<KubernetesEnvironmentResource>().Any() ||
                        ctx.Model.Resources.OfType<IComputeEnvironmentResource>()
                            .Any(r => r.HasAnnotationOfType<KubernetesEnvironmentAnnotation>());

                    if (!hasKubernetesEnvironment)
                    {
                        foreach (var r in ctx.Model.GetComputeResources())
                        {
                            if (r.HasAnnotationOfType<KubernetesServiceCustomizationAnnotation>())
                            {
                                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as a Kubernetes service, but there are no '{nameof(KubernetesEnvironmentResource)}' resources or Kubernetes-backed compute environments. Ensure you have added one by calling '{nameof(AddKubernetesEnvironment)}'.");
                            }
                        }
                    }

                    return Task.CompletedTask;
                },
                dependsOn: WellKnownPipelineSteps.ValidateComputeEnvironments,
                requiredBy: WellKnownPipelineSteps.BeforeStart);
        }

        return builder;
    }

    private static void ValidateAndFinalizePersistentVolumeBindings(PipelineStepContext context)
    {
        var bindings = GetPersistentVolumeBindings(context);

        if (context.ExecutionContext.IsRunMode)
        {
            ValidateRunModePersistentVolumeBindings(bindings);
            ApplyRunModeContainerVolumeNames(bindings);
            return;
        }

        ValidatePublishModePersistentVolumeBindings(bindings);
    }

    private static PersistentVolumeBinding[] GetPersistentVolumeBindings(PipelineStepContext context)
    {
        // GetComputeResources intentionally represents publishable workloads and excludes plain
        // executables. Run mode must inspect every compute resource because those executables can
        // consume the local IAspireStore-backed volume path.
        var computeResources = context.ExecutionContext.IsRunMode
            ? context.Model.Resources.Where(resource => resource is IComputeResource)
            : context.Model.GetComputeResources();

        return computeResources
            .SelectMany(resource =>
                resource.Annotations
                    .OfType<KubernetesPersistentVolumeBindingAnnotation>()
                    .Select(binding => new PersistentVolumeBinding(resource, binding)))
            .ToArray();
    }

    private static void ValidateRunModePersistentVolumeBindings(PersistentVolumeBinding[] bindings)
    {
        foreach (var (resource, annotation) in bindings)
        {
            if (annotation.EnvironmentVariableName is not null &&
                resource is not ProjectResource and not ExecutableResource and not ContainerResource)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' cannot resolve the '{annotation.EnvironmentVariableName}' persistent-volume path in run mode. " +
                    $"Only project, executable, and container resources are supported.");
            }
        }

        ValidateRunModeBackingStoreCompatibility(bindings);
    }

    private static void ValidateRunModeBackingStoreCompatibility(PersistentVolumeBinding[] bindings)
    {
        // Host processes use an IAspireStore directory while containers use a named runtime
        // volume. Treating those as one logical volume would silently split the data in run mode.
        foreach (var environmentGroup in bindings.GroupBy(
            item => item.Annotation.Volume.Parent.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            foreach (var volumeGroup in environmentGroup.GroupBy(
                item => item.Annotation.Volume.Name,
                StringComparer.OrdinalIgnoreCase))
            {
                var hasContainer = volumeGroup.Any(item => item.Resource is ContainerResource);
                var hasHostProcess = volumeGroup.Any(item => item.Resource is ProjectResource or ExecutableResource);

                if (hasContainer && hasHostProcess)
                {
                    var volume = volumeGroup.First().Annotation.Volume;
                    var resourceNames = string.Join(", ", volumeGroup.Select(item => $"'{item.Resource.Name}'"));
                    throw new InvalidOperationException(
                        $"Kubernetes persistent volume '{volume.Name}' is used by both local container and host-process resources ({resourceNames}). " +
                        $"Run mode cannot provide one shared backing store across those execution types. Use only containers or only projects/executables for this volume.");
                }
            }
        }
    }

    private static void ApplyRunModeContainerVolumeNames(PersistentVolumeBinding[] bindings)
    {
        foreach (var (resource, annotation) in bindings)
        {
            ApplyRunModeContainerVolumeName(resource, annotation);
        }
    }

    private static void ValidatePublishModePersistentVolumeBindings(PersistentVolumeBinding[] bindings)
    {
        foreach (var (resource, annotation) in bindings)
        {
            var targetEnvironment = resource.GetComputeEnvironment();
            var volumeEnvironment = annotation.Volume.Parent;

            // AKS owns an inner Kubernetes environment, so a binding is valid when the workload
            // targets either the Kubernetes environment directly or its owning compute environment.
            if (targetEnvironment != volumeEnvironment &&
                targetEnvironment != volumeEnvironment.OwningComputeEnvironment)
            {
                var targetName = targetEnvironment?.Name ?? "<none>";
                var supportedTargetName = (volumeEnvironment.OwningComputeEnvironment ?? volumeEnvironment).Name;
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' is assigned to compute environment '{targetName}' but binds " +
                    $"Kubernetes persistent volume '{annotation.Volume.Name}' which belongs to environment " +
                    $"'{volumeEnvironment.Name}'. A workload can only bind persistent volumes declared on its " +
                    $"Kubernetes compute environment. Declare the volume on the workload's Kubernetes environment, " +
                    $"or assign the workload to '{supportedTargetName}' with WithComputeEnvironment.");
            }
        }
    }

    private static void ApplyRunModeContainerVolumeName(
        IResource resource,
        KubernetesPersistentVolumeBindingAnnotation binding)
    {
        if (resource is not ContainerResource || binding.RunModeContainerVolumeName is not { } localVolumeName)
        {
            return;
        }

        var matchingMounts = resource.Annotations
            .OfType<ContainerMountAnnotation>()
            .Where(mount => mount.Type == ContainerMountType.Volume &&
                string.Equals(mount.Source, binding.Volume.Name, StringComparison.Ordinal))
            .ToArray();

        // Resolve after the model is complete so WithPersistentVolume and WithVolume remain
        // order-independent while publish mode can continue matching the original claim name.
        foreach (var mount in matchingMounts)
        {
            resource.Annotations.Remove(mount);
            resource.Annotations.Add(new ContainerMountAnnotation(localVolumeName, mount.Target, mount.Type, mount.IsReadOnly));
        }
    }

    private readonly record struct PersistentVolumeBinding(
        IResource Resource,
        KubernetesPersistentVolumeBindingAnnotation Annotation);

    private sealed class KubernetesPipelineStepMarker
    {
        public const string StepName = "validate-kubernetes";
    }

    /// <summary>
    /// Adds a Kubernetes environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the Kubernetes environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesEnvironmentResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

        var resourceBuilder = builder.AddResource(resource)
            .WithIconName("ServerMultiple");

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
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport(RunSyncOnBackgroundThread = true)]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
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
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// When enabled, an Aspire Dashboard container is deployed alongside the application resources
    /// in the Kubernetes cluster. All resources with OTLP telemetry support are automatically
    /// configured to send telemetry data to the dashboard.
    /// </remarks>
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Use this overload to customize the dashboard container, for example to set a specific host port
    /// or enable forwarded headers for ingress access.
    /// </remarks>
    [AspireExport("configureDashboard", MethodName = "configureDashboard", RunSyncOnBackgroundThread = true)]
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
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
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
            .WithIconName("ServerMultiple")
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
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport("withKubernetesNodePool", MethodName = "withNodePool")]
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
