// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Extension methods for <see cref="IDistributedApplicationPipeline"/>.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DistributedApplicationPipelineExtensions
{
    /// <summary>
    /// Adds a step to the pipeline and schedules it to run on the specified target.
    /// </summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="name">The unique name of the step.</param>
    /// <param name="action">The action to execute for this step.</param>
    /// <param name="scheduledBy">The target to schedule the step on.</param>
    /// <param name="dependsOn">The name of the step this step depends on, or a list of step names.</param>
    /// <param name="requiredBy">The name of the step that requires this step, or a list of step names.</param>
    public static void AddStep(
        this IDistributedApplicationPipeline pipeline,
        string name,
        Func<PipelineStepContext, Task> action,
        IPipelineStepTarget scheduledBy,
        object? dependsOn = null,
        object? requiredBy = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(scheduledBy);

        pipeline.AddStep(name, action, dependsOn, requiredBy);
        pipeline.ScheduleStep(name, scheduledBy);
    }
}
