// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Represents a job within a GitHub Actions workflow.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class GitHubActionsJobResource : Resource, IPipelineStepTarget
{
    private readonly List<string> _dependsOnJobs = [];

    internal GitHubActionsJobResource(string id, GitHubActionsWorkflowResource workflow)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(workflow);

        Id = id;
        Workflow = workflow;
    }

    /// <summary>
    /// Gets the unique identifier for this job within the workflow.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets or sets the human-readable display name for this job.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the runner label for this job (defaults to "ubuntu-latest").
    /// </summary>
    public string RunsOn { get; set; } = "ubuntu-latest";

    /// <summary>
    /// Gets the IDs of jobs that this job depends on (maps to the <c>needs:</c> key in the workflow YAML).
    /// </summary>
    public IReadOnlyList<string> DependsOnJobs => _dependsOnJobs;

    /// <summary>
    /// Gets the workflow that owns this job.
    /// </summary>
    public GitHubActionsWorkflowResource Workflow { get; }

    /// <inheritdoc />
    IPipelineEnvironment IPipelineStepTarget.Environment => Workflow;

    /// <summary>
    /// Declares that this job depends on another job.
    /// </summary>
    /// <param name="jobId">The ID of the job this job depends on.</param>
    public void DependsOn(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        _dependsOnJobs.Add(jobId);
    }

    /// <summary>
    /// Declares that this job depends on another job.
    /// </summary>
    /// <param name="job">The job this job depends on.</param>
    public void DependsOn(GitHubActionsJobResource job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _dependsOnJobs.Add(job.Id);
    }
}
