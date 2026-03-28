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
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class GitHubActionsStageResource(string name, GitHubActionsWorkflowResource workflow)
    : Resource(name)
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
}
