// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Represents a GitHub Actions workflow as a pipeline environment resource.
/// </summary>
/// <remarks>
/// A workflow can itself be used as a scheduling target via <see cref="IPipelineStepTarget"/>.
/// When a step is scheduled onto a workflow (rather than a specific job), the resolver
/// automatically creates a default stage and job to host the step.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class GitHubActionsWorkflowResource(string name) : Resource(name), IPipelineEnvironment, IPipelineStepTarget
{
    private readonly List<GitHubActionsJobResource> _jobs = [];
    private readonly List<GitHubActionsStageResource> _stages = [];

    /// <summary>
    /// Gets the filename for the generated workflow YAML file (e.g., "deploy.yml").
    /// </summary>
    public string WorkflowFileName => $"{Name}.yml";

    /// <summary>
    /// Gets the jobs declared in this workflow.
    /// </summary>
    public IReadOnlyList<GitHubActionsJobResource> Jobs => _jobs;

    /// <summary>
    /// Gets the stages declared in this workflow.
    /// </summary>
    public IReadOnlyList<GitHubActionsStageResource> Stages => _stages;

    /// <inheritdoc />
    string IPipelineStepTarget.Id => Name;

    /// <inheritdoc />
    IPipelineEnvironment IPipelineStepTarget.Environment => this;

    /// <summary>
    /// Adds a stage to this workflow. Stages are a logical grouping of jobs.
    /// </summary>
    /// <param name="name">The unique stage name within the workflow.</param>
    /// <returns>The created <see cref="GitHubActionsStageResource"/>.</returns>
    public GitHubActionsStageResource AddStage(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_stages.Any(s => s.Name == name))
        {
            throw new InvalidOperationException(
                $"A stage with the name '{name}' has already been added to the workflow '{Name}'.");
        }

        var stage = new GitHubActionsStageResource(name, this);
        _stages.Add(stage);
        return stage;
    }

    /// <summary>
    /// Adds a job to this workflow.
    /// </summary>
    /// <param name="id">The unique job identifier within the workflow.</param>
    /// <returns>The created <see cref="GitHubActionsJobResource"/>.</returns>
    public GitHubActionsJobResource AddJob(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        if (_jobs.Any(j => j.Id == id))
        {
            throw new InvalidOperationException(
                $"A job with the ID '{id}' has already been added to the workflow '{Name}'.");
        }

        var job = new GitHubActionsJobResource(id, this);
        _jobs.Add(job);
        return job;
    }

    /// <summary>
    /// Gets or creates the default stage for this workflow.
    /// </summary>
    /// <returns>The default <see cref="GitHubActionsStageResource"/>.</returns>
    internal GitHubActionsStageResource GetOrAddDefaultStage()
    {
        for (var i = 0; i < _stages.Count; i++)
        {
            if (_stages[i].Name == "default")
            {
                return _stages[i];
            }
        }

        return AddStage("default");
    }

    /// <summary>
    /// Gets or creates a default job for this workflow by delegating to the default stage.
    /// </summary>
    /// <returns>The default <see cref="GitHubActionsJobResource"/>.</returns>
    internal GitHubActionsJobResource GetOrAddDefaultJob()
    {
        var stage = GetOrAddDefaultStage();
        return stage.GetOrAddDefaultJob();
    }
}
