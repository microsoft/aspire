// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines.GitHubActions.Yaml;

/// <summary>
/// Represents a complete GitHub Actions workflow YAML document.
/// </summary>
internal sealed class WorkflowYaml
{
    public required string Name { get; init; }

    public WorkflowTriggers On { get; init; } = new();

    public Dictionary<string, string>? Permissions { get; init; }

    public Dictionary<string, JobYaml> Jobs { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Represents the trigger configuration for a workflow.
/// </summary>
internal sealed class WorkflowTriggers
{
    public bool WorkflowDispatch { get; init; } = true;

    public PushTrigger? Push { get; init; }
}

/// <summary>
/// Represents the push trigger configuration.
/// </summary>
internal sealed class PushTrigger
{
    public List<string> Branches { get; init; } = [];
}

/// <summary>
/// Represents a job in the workflow.
/// </summary>
internal sealed class JobYaml
{
    public string? Name { get; init; }

    public string RunsOn { get; init; } = "ubuntu-latest";

    public string? If { get; init; }

    public string? Environment { get; init; }

    public List<string>? Needs { get; init; }

    public Dictionary<string, string>? Permissions { get; init; }

    public Dictionary<string, string>? Env { get; init; }

    public ConcurrencyYaml? Concurrency { get; init; }

    public List<StepYaml> Steps { get; init; } = [];
}

/// <summary>
/// Represents a step within a job.
/// </summary>
internal sealed class StepYaml
{
    public string? Name { get; init; }

    public string? Uses { get; init; }

    public string? Run { get; init; }

    public Dictionary<string, string>? With { get; init; }

    public Dictionary<string, string>? Env { get; init; }

    public string? Id { get; init; }
}

/// <summary>
/// Represents concurrency configuration for a job.
/// </summary>
internal sealed class ConcurrencyYaml
{
    public required string Group { get; init; }

    public bool CancelInProgress { get; init; }
}
