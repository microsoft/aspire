// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Tests;

/// <summary>
/// Helper utilities for Radius integration tests.
/// </summary>
internal static class RadiusTestHelper
{
    /// <summary>
    /// Simulates the <c>prepare-deployment-targets-{name}</c> pipeline step
    /// (<see cref="RadiusInfrastructure.PrepareDeploymentTargetsAsync(RadiusEnvironmentResource, Aspire.Hosting.Pipelines.PipelineStepContext)"/>)
    /// by attaching a <see cref="DeploymentTargetAnnotation"/> for <paramref name="environment"/>
    /// to the compute resources that belong to it.
    /// </summary>
    /// <remarks>
    /// Publishing-context unit tests construct <c>RadiusBicepPublishingContext</c>
    /// directly and don't execute the pipeline. The publishing context is strict about only
    /// emitting resources explicitly targeted at the environment, so tests that exercise
    /// <c>GenerateBicep</c> must mimic what the prepare step would have done in a real run.
    /// This mirrors the real step's targeting rules: resources explicitly bound to a different
    /// compute environment via <c>WithComputeEnvironment</c> are skipped, and the annotation's
    /// <see cref="DeploymentTargetAnnotation.ComputeEnvironment"/> is set to the environment's
    /// <see cref="RadiusEnvironmentResource.OwningComputeEnvironment"/> parent when present.
    /// Keep this in sync with <c>PrepareDeploymentTargetsAsync</c>.
    /// </remarks>
    public static void AttachDeploymentTargets(
        RadiusEnvironmentResource environment,
        DistributedApplicationModel model)
    {
        // A child-of-parent-compute-environment (AKS-on-K8s) environment has resources that
        // target the parent rather than the Radius environment directly; match either.
        var targetComputeEnvironment = environment.OwningComputeEnvironment ?? (IComputeEnvironmentResource)environment;

        foreach (var resource in model.GetComputeResources())
        {
            var resourceComputeEnvironment = resource.GetComputeEnvironment();

            // Skip resources explicitly targeted at a different compute environment (via
            // WithComputeEnvironment). A null value means there's a single compute environment
            // in the model — which is this one — so it is not skipped.
            if (resourceComputeEnvironment is not null &&
                resourceComputeEnvironment != environment &&
                resourceComputeEnvironment != environment.OwningComputeEnvironment)
            {
                continue;
            }

            var alreadyTargeted = resource.Annotations
                .OfType<DeploymentTargetAnnotation>()
                .Any(a => a.ComputeEnvironment == targetComputeEnvironment);
            if (alreadyTargeted)
            {
                continue;
            }

            resource.Annotations.Add(new DeploymentTargetAnnotation(environment)
            {
                ComputeEnvironment = targetComputeEnvironment
            });
        }
    }
}
