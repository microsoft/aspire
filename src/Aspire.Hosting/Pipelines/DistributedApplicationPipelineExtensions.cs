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
    /// Configures an action that runs after the named pipeline step and all of its dependencies complete.
    /// </summary>
    /// <param name="pipeline">The distributed application pipeline.</param>
    /// <param name="stepName">The name of the pipeline step.</param>
    /// <param name="action">A final action to execute.</param>
    /// <returns>The distributed application pipeline for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="pipeline"/> or <paramref name="action"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stepName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown during pipeline resolution when the named step does not exist.
    /// </exception>
    /// <remarks>
    /// The final action is configured when the pipeline graph is resolved, so it runs after dependencies
    /// added by resource annotations and other pipeline configuration callbacks. Multiple final actions
    /// for the same step execute sequentially in registration order. If a final action fails, subsequent
    /// final actions are not invoked.
    /// </remarks>
    [AspireExportIgnore(Reason = "Delegate callbacks are not ATS-compatible.")]
    public static IDistributedApplicationPipeline WithFinalAction(
        this IDistributedApplicationPipeline pipeline,
        string stepName,
        Func<PipelineStepContext, Task> action)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        ArgumentNullException.ThrowIfNull(action);

        pipeline.AddPipelineConfiguration(context =>
        {
            var step = context.Steps.FirstOrDefault(candidate => candidate.Name == stepName);
            if (step is null)
            {
                var availableSteps = string.Join(", ", context.Steps.Select(candidate => $"'{candidate.Name}'"));
                throw new InvalidOperationException(
                    $"Step '{stepName}' not found in pipeline. Available steps: {availableSteps}");
            }

            step.AddFinalAction(action);
            return Task.CompletedTask;
        });

        return pipeline;
    }

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
    [AspireExport]
    public static IDistributedApplicationPipeline DisableBuildOnlyContainerValidation(this IDistributedApplicationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        pipeline.AddPipelineConfiguration(static context =>
        {
            var validationStep = context.Steps.FirstOrDefault(step => step.Name == DistributedApplicationPipeline.ValidateBuildOnlyContainerReferencesStepName);
            validationStep?.RequiredBySteps.Clear();
            return Task.CompletedTask;
        });

        return pipeline;
    }
}
