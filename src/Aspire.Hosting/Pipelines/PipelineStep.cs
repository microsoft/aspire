// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a step in the deployment pipeline.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[DebuggerDisplay("{DebuggerToString(),nq}")]
[AspireExport(ExposeProperties = true)]
public class PipelineStep
{
    /// <summary>
    /// Gets or initializes the unique name of the step.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or initializes the description of the step.
    /// </summary>
    /// <remarks>
    /// The description provides human-readable context about what the step does,
    /// helping users and tools understand the purpose of the step.
    /// </remarks>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or initializes the action to execute for this step.
    /// </summary>
    public required Func<PipelineStepContext, Task> Action { get; init; }

    /// <summary>
    /// Gets or initializes the list of step names that this step depends on.
    /// </summary>
    public List<string> DependsOnSteps { get; init; } = [];

    /// <summary>
    /// Gets or initializes the list of step names that require this step to complete before they can finish.
    /// This is used internally during pipeline construction and is converted to DependsOn relationships.
    /// </summary>
    public List<string> RequiredBySteps { get; init; } = [];

    /// <summary>
    /// Gets or initializes the list of tags that categorize this step.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// Gets or initializes the resource that this step is associated with, if any.
    /// </summary>
    public IResource? Resource { get; set; }

    /// <summary>
    /// Adds a dependency on another step.
    /// </summary>
    /// <param name="stepName">The name of the step to depend on.</param>
    [AspireExport("dependsOn", Description = "Adds a dependency on another step by name")]
    public void DependsOn(string stepName)
    {
        DependsOnSteps.Add(stepName);
    }

    /// <summary>
    /// Adds a dependency on another step.
    /// </summary>
    /// <param name="step">The step to depend on.</param>
    public void DependsOn(PipelineStep step)
    {
        DependsOnSteps.Add(step.Name);
    }

    /// <summary>
    /// Specifies that this step is required by another step.
    /// This creates the inverse relationship where the other step will depend on this step.
    /// </summary>
    /// <param name="stepName">The name of the step that requires this step.</param>
    [AspireExport("requiredBy", Description = "Specifies that another step requires this step by name")]
    public void RequiredBy(string stepName)
    {
        RequiredBySteps.Add(stepName);
    }

    /// <summary>
    /// Specifies that this step is required by another step.
    /// This creates the inverse relationship where the other step will depend on this step.
    /// </summary>
    /// <param name="step">The step that requires this step.</param>
    public void RequiredBy(PipelineStep step)
    {
        RequiredBySteps.Add(step.Name);
    }

    private string DebuggerToString()
    {
        var dependsOnSteps = DependsOnSteps.Count > 0 ? string.Join(',', DependsOnSteps.Select(s => $@"""{s}""")) : "None";
        var requiredBySteps = RequiredBySteps.Count > 0 ? string.Join(',', RequiredBySteps.Select(s => $@"""{s}""")) : "None";

        return $@"Name = ""{Name}"", DependsOnSteps = {dependsOnSteps}, RequiredBySteps = {requiredBySteps}";
    }
}
