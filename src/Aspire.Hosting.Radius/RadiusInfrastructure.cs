// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Pipeline-step helper invoked by the per-environment
/// <c>prepare-deployment-targets-{name}</c> step. Materializes
/// <see cref="DeploymentTargetAnnotation"/> instances on compute resources
/// scoped to a specific <see cref="RadiusEnvironmentResource"/>.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the K8s/Docker compute-environment integrations
/// (see <c>KubernetesEnvironmentResource.PrepareDeploymentTargetsAsync</c>
/// and <c>DockerComposeEnvironmentResource.PrepareDeploymentTargetsAsync</c>):
/// the step <c>DependsOn(ValidateComputeEnvironments)</c> and
/// <c>RequiredBy(BeforeStart)</c>, so ambiguity across multiple compute
/// environments is rejected by the framework before this code runs (see
/// <c>DistributedApplicationPipeline.ValidateComputeEnvironmentBindings</c>).
/// </para>
/// <para>
/// In <c>Run</c> mode the prepare step is a no-op so the integration adds
/// no inner-loop overhead — matching K8s, which doesn't even register its
/// environment resource in run mode.
/// </para>
/// </remarks>
internal static class RadiusInfrastructure
{
    /// <summary>
    /// Materializes <see cref="DeploymentTargetAnnotation"/> instances on every
    /// compute resource whose target compute environment is <paramref name="environment"/>
    /// (or its <see cref="RadiusEnvironmentResource.OwningComputeEnvironment"/> parent).
    /// Resources targeted to a different environment are left untouched so that
    /// sibling environments (e.g. another Radius env, K8s, Docker Compose) can
    /// claim them via their own prepare step.
    /// </summary>
    internal static Task PrepareDeploymentTargetsAsync(
        RadiusEnvironmentResource environment,
        PipelineStepContext context)
    {
        // No work to do in Run mode. The environment resource is not registered with
        // the application builder in Run mode either (see AddRadiusEnvironment), but
        // guard here regardless in case a caller invokes the prepare step directly
        // for a manually-constructed environment.
        if (context.ExecutionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RadiusInfrastructure).FullName!);

        logger.LogInformation(
            "Preparing deployment targets for Radius environment '{EnvironmentName}' in namespace '{Namespace}'",
            environment.Name,
            environment.Namespace);

        // When this environment is a child of a parent compute environment (the AKS-on-K8s
        // pattern, see KubernetesEnvironmentResource.cs:130-140), resources may target the
        // parent rather than us directly. Match either.
        var targetComputeEnvironment = environment.OwningComputeEnvironment ?? environment;

        foreach (var resource in context.Model.GetComputeResources())
        {
            var resourceComputeEnvironment = resource.GetComputeEnvironment();

            // Skip resources that are explicitly targeted to a different compute environment.
            // ValidateComputeEnvironments (a DependsOn for this step) already fails-fast on
            // untargeted resources when multiple compute environments exist, so a null value
            // here means there's exactly one compute environment in the model — which is us.
            if (resourceComputeEnvironment is not null &&
                resourceComputeEnvironment != environment &&
                resourceComputeEnvironment != environment.OwningComputeEnvironment)
            {
                continue;
            }

            // Skip if a target annotation for this environment already exists. Prepare steps
            // are idempotent so re-execution (e.g. during test composition) does not duplicate.
            var existingAnnotations = resource.Annotations
                .OfType<DeploymentTargetAnnotation>()
                .Any(a => a.ComputeEnvironment == targetComputeEnvironment);
            if (existingAnnotations)
            {
                continue;
            }

            // ContainerRegistry is intentionally left null in this prototype: the integration
            // does not yet build/push images, so the standard push-prereq chain has no work
            // to do. When project-publish support lands (see README "Limitations") this
            // should read from a registry annotation on the environment, the same way
            // KubernetesEnvironmentResource.GetContainerRegistry does.
            resource.Annotations.Add(new DeploymentTargetAnnotation(environment)
            {
                ComputeEnvironment = targetComputeEnvironment
            });

            logger.LogDebug(
                "Attached deployment target annotation to resource '{ResourceName}' targeting Radius environment '{EnvironmentName}'",
                resource.Name,
                environment.Name);
        }

        return Task.CompletedTask;
    }
}
