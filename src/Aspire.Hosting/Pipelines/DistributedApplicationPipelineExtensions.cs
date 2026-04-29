// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Extension methods for <see cref="IDistributedApplicationPipeline"/>.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DistributedApplicationPipelineExtensions
{
    /// <summary>
    /// Disables the publish and deploy validation that requires build-only containers to be consumed by another resource.
    /// </summary>
    /// <param name="pipeline">The distributed application pipeline.</param>
    /// <returns>The distributed application pipeline for chaining.</returns>
    /// <remarks>
    /// This is an application-wide escape hatch for scenarios where the build-only container validation is too restrictive
    /// for a particular app. Prefer wiring build-only containers through <c>PublishWithContainerFiles</c> or
    /// <c>PublishWithStaticFiles</c> when possible.
    /// </remarks>
    [AspireExport(Description = "Disables publish and deploy validation for unconsumed build-only containers.")]
    public static IDistributedApplicationPipeline DisableBuildOnlyContainerValidation(this IDistributedApplicationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        pipeline.AddPipelineConfiguration(static context =>
        {
            var validationStep = context.Steps.Single(step => step.Name == DistributedApplicationPipeline.ValidateBuildOnlyContainerReferencesStepName);
            validationStep.RequiredBySteps.Clear();
            return Task.CompletedTask;
        });

        return pipeline;
    }
}
