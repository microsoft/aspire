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
    /// to every compute resource that doesn't already have one targeting this environment.
    /// </summary>
    /// <remarks>
    /// Publishing-context unit tests construct <c>RadiusBicepPublishingContext</c>
    /// directly and don't execute the pipeline. After the A1/A2 fix, the publishing context
    /// is strict about only emitting resources explicitly targeted at the environment, so
    /// tests that exercise <c>GenerateBicep</c> must mimic what the prepare step would
    /// have done in a real run. The architectural review's finding I5 tracks replacing
    /// this workaround with a full-pipeline integration test.
    /// </remarks>
    public static void AttachDeploymentTargets(
        RadiusEnvironmentResource environment,
        DistributedApplicationModel model)
    {
        foreach (var resource in model.GetComputeResources())
        {
            var alreadyTargeted = resource.Annotations
                .OfType<DeploymentTargetAnnotation>()
                .Any(a => a.ComputeEnvironment == environment);
            if (alreadyTargeted)
            {
                continue;
            }

            resource.Annotations.Add(new DeploymentTargetAnnotation(environment)
            {
                ComputeEnvironment = environment
            });
        }
    }
}
