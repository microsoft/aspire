// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines.GitHubActions.Yaml;

/// <summary>
/// Represents a complete GitHub Actions workflow YAML document.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class WorkflowYaml
{
    /// <summary>
    /// Gets the name of the workflow.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the trigger configuration for the workflow.
    /// </summary>
    public WorkflowTriggers On { get; init; } = new();

    /// <summary>
    /// Gets the top-level permissions for the workflow.
    /// </summary>
    public Dictionary<string, string>? Permissions { get; init; }

    /// <summary>
    /// Gets the jobs defined in the workflow, keyed by job ID.
    /// </summary>
    public Dictionary<string, JobYaml> Jobs { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Represents the trigger configuration for a workflow.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class WorkflowTriggers
{
    /// <summary>
    /// Gets a value indicating whether the workflow can be manually triggered.
    /// </summary>
    public bool WorkflowDispatch { get; init; } = true;

    /// <summary>
    /// Gets the push trigger configuration.
    /// </summary>
    public PushTrigger? Push { get; init; }
}

/// <summary>
/// Represents the push trigger configuration.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PushTrigger
{
    /// <summary>
    /// Gets the branch patterns that trigger the workflow on push.
    /// </summary>
    public List<string> Branches { get; init; } = [];
}

/// <summary>
/// Represents a job in the workflow.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class JobYaml
{
    /// <summary>
    /// Gets the display name of the job.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the runner label for the job (e.g., "ubuntu-latest").
    /// </summary>
    public string RunsOn { get; init; } = "ubuntu-latest";

    /// <summary>
    /// Gets the conditional expression controlling whether the job runs.
    /// </summary>
    public string? If { get; init; }

    /// <summary>
    /// Gets the GitHub deployment environment name for the job.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Gets the list of job IDs that this job depends on.
    /// </summary>
    public List<string>? Needs { get; init; }

    /// <summary>
    /// Gets the permissions granted to the job.
    /// </summary>
    public Dictionary<string, string>? Permissions { get; init; }

    /// <summary>
    /// Gets the environment variables for the job.
    /// </summary>
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Gets the concurrency configuration for the job.
    /// </summary>
    public ConcurrencyYaml? Concurrency { get; init; }

    /// <summary>
    /// Gets the steps in the job.
    /// </summary>
    public List<StepYaml> Steps { get; init; } = [];
}

/// <summary>
/// Represents a step within a job.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class StepYaml
{
    /// <summary>
    /// Gets the display name of the step.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the action reference (e.g., "actions/checkout@v4").
    /// </summary>
    public string? Uses { get; init; }

    /// <summary>
    /// Gets the shell command to run.
    /// </summary>
    public string? Run { get; init; }

    /// <summary>
    /// Gets the input parameters passed to the action.
    /// </summary>
    public Dictionary<string, string>? With { get; init; }

    /// <summary>
    /// Gets the environment variables for the step.
    /// </summary>
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Gets the step identifier for referencing in expressions.
    /// </summary>
    public string? Id { get; init; }
}

/// <summary>
/// Represents concurrency configuration for a job.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ConcurrencyYaml
{
    /// <summary>
    /// Gets the concurrency group name.
    /// </summary>
    public required string Group { get; init; }

    /// <summary>
    /// Gets a value indicating whether to cancel in-progress runs in the same concurrency group.
    /// </summary>
    public bool CancelInProgress { get; init; }
}
