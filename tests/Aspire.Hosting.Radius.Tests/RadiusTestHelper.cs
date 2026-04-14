// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests;

/// <summary>
/// Helper utilities for Radius integration tests.
/// </summary>
internal static class RadiusTestHelper
{
    /// <summary>
    /// Builds an app host and returns its <see cref="DistributedApplicationModel"/>.
    /// </summary>
    public static DistributedApplicationModel BuildAndGetModel(Action<IDistributedApplicationBuilder> configure)
    {
        var builder = DistributedApplication.CreateBuilder();
        configure(builder);
        using var app = builder.Build();
        return app.Services.GetRequiredService<DistributedApplicationModel>();
    }

    /// <summary>
    /// Gets the first <see cref="RadiusEnvironmentResource"/> from the model.
    /// </summary>
    public static RadiusEnvironmentResource GetRadiusEnvironment(DistributedApplicationModel model)
    {
        return model.Resources.OfType<RadiusEnvironmentResource>().First();
    }

    /// <summary>
    /// Gets all <see cref="DeploymentTargetAnnotation"/> instances on a resource.
    /// </summary>
    public static IEnumerable<DeploymentTargetAnnotation> GetDeploymentTargetAnnotations(IResource resource)
    {
        return resource.Annotations.OfType<DeploymentTargetAnnotation>();
    }

    /// <summary>
    /// Simulates the <c>prepare-deployment-targets-{name}</c> pipeline step
    /// (<see cref="RadiusInfrastructure.PrepareDeploymentTargetsAsync(RadiusEnvironmentResource, Aspire.Hosting.Pipelines.PipelineStepContext)"/>)
    /// by attaching a <see cref="DeploymentTargetAnnotation"/> for <paramref name="environment"/>
    /// to every compute resource that doesn't already have one targeting this environment.
    /// </summary>
    /// <remarks>
    /// Publishing-context unit tests construct <see cref="RadiusBicepPublishingContext"/>
    /// directly and don't execute the pipeline. After the A1/A2 fix, the publishing context
    /// is strict about only emitting resources explicitly targeted at the environment, so
    /// tests that exercise <see cref="RadiusBicepPublishingContext.GenerateBicep(DistributedApplicationModel, Microsoft.Extensions.Logging.ILogger?)"/>
    /// must mimic what the prepare step would have done in a real run.
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
