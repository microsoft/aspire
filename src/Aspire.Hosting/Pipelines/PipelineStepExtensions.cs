// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Extension methods for pipeline steps.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class PipelineStepExtensions
{
    /// <summary>
    /// Makes each step in the collection depend on the specified step.
    /// </summary>
    /// <param name="steps">The collection of steps.</param>
    /// <param name="step">The step to depend on.</param>
    /// <returns>The original collection of steps.</returns>
    [AspireExportIgnore(Reason = "Use PipelineStep.dependsOn on each step in the collection, or mutate PipelineStep.dependsOnSteps directly.")]
    public static IEnumerable<PipelineStep> DependsOn(this IEnumerable<PipelineStep> steps, PipelineStep? step)
    {
        if (step is null)
        {
            return steps;
        }

        foreach (var s in steps)
        {
            s.DependsOn(step);
        }

        return steps;
    }

    /// <summary>
    /// Makes each step in the collection depend on the specified step name.
    /// </summary>
    /// <param name="steps">The collection of steps.</param>
    /// <param name="stepName">The name of the step to depend on.</param>
    /// <returns>The original collection of steps.</returns>
    [AspireExportIgnore(Reason = "Use PipelineStep.dependsOn on each step in the collection, or mutate PipelineStep.dependsOnSteps directly.")]
    public static IEnumerable<PipelineStep> DependsOn(this IEnumerable<PipelineStep> steps, string stepName)
    {
        if (string.IsNullOrEmpty(stepName))
        {
            return steps;
        }

        foreach (var s in steps)
        {
            s.DependsOn(stepName);
        }

        return steps;
    }

    /// <summary>
    /// Makes each step in the collection depend on the specified target steps.
    /// </summary>
    /// <param name="steps">The collection of steps.</param>
    /// <param name="targetSteps">The target steps to depend on.</param>
    /// <returns>The original collection of steps.</returns>
    [AspireExportIgnore(Reason = "Use PipelineStep.dependsOn on each step in the collection, or mutate PipelineStep.dependsOnSteps directly.")]
    public static IEnumerable<PipelineStep> DependsOn(this IEnumerable<PipelineStep> steps, IEnumerable<PipelineStep> targetSteps)
    {
        foreach (var step in targetSteps)
        {
            foreach (var s in steps)
            {
                s.DependsOn(step);
            }
        }

        return steps;
    }

    /// <summary>
    /// Specifies that each step in the collection is required by the specified step.
    /// </summary>
    /// <param name="steps">The collection of steps.</param>
    /// <param name="step">The step that requires these steps.</param>
    /// <returns>The original collection of steps.</returns>
    [AspireExportIgnore(Reason = "Use PipelineStep.requiredBy on each step in the collection, or mutate PipelineStep.requiredBySteps directly.")]
    public static IEnumerable<PipelineStep> RequiredBy(this IEnumerable<PipelineStep> steps, PipelineStep? step)
    {
        if (step is null)
        {
            return steps;
        }

        foreach (var s in steps)
        {
            s.RequiredBy(step);
        }

        return steps;
    }

    /// <summary>
    /// Specifies that each step in the collection is required by the specified step name.
    /// </summary>
    /// <param name="steps">The collection of steps.</param>
    /// <param name="stepName">The name of the step that requires these steps.</param>
    /// <returns>The original collection of steps.</returns>
    [AspireExportIgnore(Reason = "Use PipelineStep.requiredBy on each step in the collection, or mutate PipelineStep.requiredBySteps directly.")]
    public static IEnumerable<PipelineStep> RequiredBy(this IEnumerable<PipelineStep> steps, string stepName)
    {
        if (string.IsNullOrEmpty(stepName))
        {
            return steps;
        }

        foreach (var s in steps)
        {
            s.RequiredBy(stepName);
        }

        return steps;
    }

    /// <summary>
    /// Specifies that each step in the collection is required by the specified target steps.
    /// </summary>
    /// <param name="steps">The collection of steps.</param>
    /// <param name="targetSteps">The target steps that require these steps.</param>
    /// <returns>The original collection of steps.</returns>
    [AspireExportIgnore(Reason = "Use PipelineStep.requiredBy on each step in the collection, or mutate PipelineStep.requiredBySteps directly.")]
    public static IEnumerable<PipelineStep> RequiredBy(this IEnumerable<PipelineStep> steps, IEnumerable<PipelineStep> targetSteps)
    {
        foreach (var step in targetSteps)
        {
            foreach (var s in steps)
            {
                s.RequiredBy(step);
            }
        }

        return steps;
    }
}
