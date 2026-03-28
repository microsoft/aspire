// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a pipeline for executing deployment steps in a distributed application.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IDistributedApplicationPipeline
{
    /// <summary>
    /// Adds a deployment step to the pipeline.
    /// </summary>
    /// <param name="name">The unique name of the step.</param>
    /// <param name="action">The action to execute for this step.</param>
    /// <param name="dependsOn">The name of the step this step depends on, or a list of step names.</param>
    /// <param name="requiredBy">The name of the step that requires this step, or a list of step names.</param>
    void AddStep(string name,
                 Func<PipelineStepContext, Task> action,
                 object? dependsOn = null,
                 object? requiredBy = null);

    /// <summary>
    /// Adds a deployment step to the pipeline.
    /// </summary>
    /// <param name="step">The pipeline step to add.</param>
    void AddStep(PipelineStep step);

    /// <summary>
    /// Registers a callback to be executed during the pipeline configuration phase.
    /// </summary>
    /// <param name="callback">The callback function to execute during the configuration phase.</param>
    void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback);

    /// <summary>
    /// Executes all steps in the pipeline in dependency order.
    /// </summary>
    /// <param name="context">The pipeline context for the execution.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ExecuteAsync(PipelineContext context);
}
