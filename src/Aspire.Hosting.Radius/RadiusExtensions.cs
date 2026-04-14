// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding and configuring Radius environment resources.
/// </summary>
public static partial class RadiusExtensions
{
    private const int KubernetesNamespaceMaxLength = 63;

    /// <summary>
    /// Adds a Radius compute environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the Radius environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RadiusEnvironmentResource}"/>.</returns>
    /// <remarks>
    /// In <see cref="DistributedApplicationOperation.Run"/> mode this returns an
    /// unregistered builder so the environment does not surface as a resource in
    /// the dashboard and no pipeline steps are wired up — matching
    /// <c>AddKubernetesEnvironment</c> and <c>AddDockerComposeEnvironment</c>.
    /// All deployment-target wiring runs in Publish mode only.
    /// </remarks>
    [AspireExport(Description = "Adds a Radius publishing environment")]
    public static IResourceBuilder<RadiusEnvironmentResource> AddRadiusEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name = "radius")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new RadiusEnvironmentResource(name);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Return a builder that isn't added to the top-level application builder so it
            // doesn't surface as a resource. The Radius integration is publish/deploy-only
            // today; Run mode has nothing to wire up.
            return builder.CreateResourceBuilder(resource);
        }

        // Per-environment prepare step: materializes DeploymentTargetAnnotations on
        // compute resources scoped to this environment. Modelled after K8s/Docker so
        // ValidateComputeEnvironments (a DependsOn) fails-fast on multi-env ambiguity
        // before this step runs, and so the step is part of the standard BeforeStart
        // synchronization point downstream code observes.
        resource.Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var step = new PipelineStep
            {
                Name = $"prepare-deployment-targets-{name}",
                Description = $"Prepares Radius deployment targets for {name}.",
                Action = stepContext => RadiusInfrastructure.PrepareDeploymentTargetsAsync(resource, stepContext),
                DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart],
            };
            return step;
        }));

        // Bicep publish step
        resource.Annotations.Add(new PipelineStepAnnotation(_ =>
            new RadiusBicepPublishingContext(resource).CreatePipelineStep()));

        // rad CLI deploy step
        resource.Annotations.Add(new PipelineStepAnnotation(_ =>
            new RadiusDeploymentPipelineStep(resource).CreatePipelineStep()));

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Sets the Kubernetes namespace for the Radius environment.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="kubernetesNamespace">A valid RFC 1123 namespace name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RadiusEnvironmentResource}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the namespace is not a valid RFC 1123 label.</exception>
    [AspireExport(Description = "Sets the Kubernetes namespace for the Radius environment")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithNamespace(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        string kubernetesNamespace)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(kubernetesNamespace);

        if (kubernetesNamespace.Length > KubernetesNamespaceMaxLength || !DnsLabelPattern().IsMatch(kubernetesNamespace))
        {
            throw new ArgumentException(
                $"Kubernetes namespace '{kubernetesNamespace}' is invalid. " +
                "Must match RFC 1123: lowercase alphanumeric characters or hyphens, " +
                $"start and end with an alphanumeric character, and be at most {KubernetesNamespaceMaxLength} characters.",
                nameof(kubernetesNamespace));
        }

        builder.Resource.Namespace = kubernetesNamespace;
        return builder;
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();

    /// <summary>
    /// Opts the environment into emitting container workloads as legacy
    /// <c>Applications.Core/containers@2023-10-01-preview</c> resources instead
    /// of <c>Radius.Compute/containers</c> (UDT). The legacy container type
    /// ships with built-in Kubernetes deployment behaviour, so it deploys
    /// without requiring a recipe to be registered in the target Radius
    /// environment. Use this fallback when the target install does not yet
    /// have a recipe registered for the <c>Radius.Compute/containers</c> UDT.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RadiusEnvironmentResource}"/>.</returns>
    [AspireExport(Description = "Emits container workloads as legacy Applications.Core/containers instead of Radius.Compute/containers")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithLegacyContainers(
        this IResourceBuilder<RadiusEnvironmentResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.UseLegacyContainers = true;
        return builder;
    }
}
