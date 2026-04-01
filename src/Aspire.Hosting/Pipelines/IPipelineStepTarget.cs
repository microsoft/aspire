// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a target that pipeline steps can be scheduled onto, such as a job
/// in a CI/CD workflow.
/// </summary>
/// <remarks>
/// When a pipeline step has a <see cref="PipelineStep.ScheduledBy"/> value, it indicates
/// that the step should execute in the context of the specified target (e.g., a specific
/// job in a GitHub Actions workflow). The scheduling resolver validates that step-to-target
/// assignments are consistent with the step dependency graph.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineStepTarget
{
    /// <summary>
    /// Gets the unique identifier for this target within its pipeline environment.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the pipeline environment that owns this target.
    /// </summary>
    IPipelineEnvironment Environment { get; }
}
