// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines.GitHubActions.Yaml;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Generates a <see cref="WorkflowYaml"/> from a scheduling result and workflow resource.
/// </summary>
internal static class WorkflowYamlGenerator
{
    private const string StateArtifactPrefix = "aspire-state-";
    private const string StatePath = ".aspire/state/";

    /// <summary>
    /// Generates a workflow YAML model from the scheduling result.
    /// </summary>
    public static WorkflowYaml Generate(SchedulingResult scheduling, GitHubActionsWorkflowResource workflow)
    {
        ArgumentNullException.ThrowIfNull(scheduling);
        ArgumentNullException.ThrowIfNull(workflow);

        var workflowYaml = new WorkflowYaml
        {
            Name = workflow.Name,
            On = new WorkflowTriggers
            {
                WorkflowDispatch = true,
                Push = new PushTrigger
                {
                    Branches = ["main"]
                }
            },
            Permissions = new Dictionary<string, string>
            {
                ["contents"] = "read",
                ["id-token"] = "write"
            }
        };

        // Generate a YAML job for each workflow job
        foreach (var job in workflow.Jobs)
        {
            var jobYaml = GenerateJob(job, scheduling);
            workflowYaml.Jobs[job.Id] = jobYaml;
        }

        return workflowYaml;
    }

    private static JobYaml GenerateJob(GitHubActionsJobResource job, SchedulingResult scheduling)
    {
        var steps = new List<StepYaml>();

        // Boilerplate: checkout
        steps.Add(new StepYaml
        {
            Name = "Checkout code",
            Uses = "actions/checkout@v4"
        });

        // Boilerplate: setup .NET
        steps.Add(new StepYaml
        {
            Name = "Setup .NET",
            Uses = "actions/setup-dotnet@v4",
            With = new Dictionary<string, string>
            {
                ["dotnet-version"] = "10.0.x"
            }
        });

        // Boilerplate: install Aspire CLI
        steps.Add(new StepYaml
        {
            Name = "Install Aspire CLI",
            Run = "dotnet tool install -g aspire"
        });

        // Download state artifacts from dependency jobs
        var jobDeps = scheduling.JobDependencies.GetValueOrDefault(job.Id);
        if (jobDeps is { Count: > 0 })
        {
            foreach (var depJobId in jobDeps)
            {
                steps.Add(new StepYaml
                {
                    Name = $"Download state from {depJobId}",
                    Uses = "actions/download-artifact@v4",
                    With = new Dictionary<string, string>
                    {
                        ["name"] = $"{StateArtifactPrefix}{depJobId}",
                        ["path"] = StatePath
                    }
                });
            }
        }

        // TODO: Auth/setup steps will be added here when PipelineSetupRequirementAnnotation is implemented.
        // For now, users should add cloud-specific authentication steps manually.

        // Run aspire do for this job's steps
        steps.Add(new StepYaml
        {
            Name = "Run pipeline steps",
            Run = $"aspire do --continue --job {job.Id}",
            Env = new Dictionary<string, string>
            {
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
            }
        });

        // Upload state artifacts for downstream jobs
        steps.Add(new StepYaml
        {
            Name = "Upload state",
            Uses = "actions/upload-artifact@v4",
            With = new Dictionary<string, string>
            {
                ["name"] = $"{StateArtifactPrefix}{job.Id}",
                ["path"] = StatePath,
                ["if-no-files-found"] = "ignore"
            }
        });

        // Build needs list from scheduling result
        List<string>? needs = null;
        if (scheduling.JobDependencies.TryGetValue(job.Id, out var deps) && deps.Count > 0)
        {
            needs = [.. deps];
        }

        return new JobYaml
        {
            Name = job.DisplayName,
            RunsOn = job.RunsOn,
            Needs = needs,
            Steps = steps
        };
    }
}
