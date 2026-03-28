// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Represents a stage within a GitHub Actions workflow. Stages are a logical grouping
/// of jobs. GitHub Actions does not have a native stage concept, so stages map to
/// a set of jobs with a shared naming prefix and implicit dependencies.
/// </summary>
/// <remarks>
/// A stage can itself be used as a scheduling target via <see cref="IPipelineStepTarget"/>.
/// When a step is scheduled onto a stage (rather than a specific job), the resolver
/// automatically creates a default job within the stage to host the step.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class GitHubActionsStageResource(string name, GitHubActionsWorkflowResource workflow)
    : Resource(name), IPipelineStepTarget
{
    private readonly List<GitHubActionsJobResource> _jobs = [];

    /// <summary>
    /// Gets the workflow that owns this stage.
    /// </summary>
    public GitHubActionsWorkflowResource Workflow { get; } = workflow ?? throw new ArgumentNullException(nameof(workflow));

    /// <summary>
    /// Gets the jobs declared in this stage.
    /// </summary>
    public IReadOnlyList<GitHubActionsJobResource> Jobs => _jobs;

    /// <inheritdoc />
    string IPipelineStepTarget.Id => Name;

    /// <inheritdoc />
    IPipelineEnvironment IPipelineStepTarget.Environment => Workflow;

    /// <summary>
    /// Adds a job to this stage.
    /// </summary>
    /// <param name="id">The unique job identifier within the workflow.</param>
    /// <returns>The created <see cref="GitHubActionsJobResource"/>.</returns>
    public GitHubActionsJobResource AddJob(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var job = Workflow.AddJob(id);
        _jobs.Add(job);
        return job;
    }

    /// <summary>
    /// Gets or creates a default job for this stage.
    /// </summary>
    /// <returns>The default <see cref="GitHubActionsJobResource"/> for this stage.</returns>
    internal GitHubActionsJobResource GetOrAddDefaultJob()
    {
        var defaultId = Name == "default" ? "default" : $"{Name}-default";

        for (var i = 0; i < _jobs.Count; i++)
        {
            if (_jobs[i].Id == defaultId)
            {
                return _jobs[i];
            }
        }

        return AddJob(defaultId);
    }
}
