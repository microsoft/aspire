// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Text.Json;
using Aspire.Hosting.Pipelines.GitHubActions.Yaml;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Generates a <see cref="WorkflowYaml"/> from a scheduling result and workflow resource.
/// </summary>
internal static class WorkflowYamlGenerator
{
    private const string StateArtifactPrefix = "aspire-do-state-";
    private const string StatePathExpression = ".aspire/state/${{ github.run_id }}-${{ github.run_attempt }}/";

    /// <summary>
    /// Generates a workflow YAML model from the scheduling result.
    /// </summary>
    public static WorkflowYaml Generate(SchedulingResult scheduling, GitHubActionsWorkflowResource workflow, string? repositoryRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(scheduling);
        ArgumentNullException.ThrowIfNull(workflow);

        var channel = ReadChannelFromConfig(repositoryRootDirectory);

        var workflowYaml = new WorkflowYaml
        {
            Name = workflow.Name,
            On = new WorkflowTriggers
            {
                WorkflowDispatch = true,
                Push = new PushTrigger
                {
                    Branches = ["main"]
                },
                PullRequest = new PullRequestTrigger
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
            var jobYaml = GenerateJob(job, scheduling, workflow, channel);
            workflowYaml.Jobs[job.Id] = jobYaml;
        }

        return workflowYaml;
    }

    private static JobYaml GenerateJob(GitHubActionsJobResource job, SchedulingResult scheduling, GitHubActionsWorkflowResource workflow, string? channel)
    {
        // Collect dependency tags from all pipeline steps assigned to this job
        var dependencyTags = new HashSet<string>(StringComparer.Ordinal);
        if (scheduling.StepsPerJob.TryGetValue(job.Id, out var pipelineSteps))
        {
            foreach (var step in pipelineSteps)
            {
                foreach (var tag in step.Tags)
                {
                    dependencyTags.Add(tag);
                }
            }
        }

        // Every job runs `aspire do`, which implicitly requires the Aspire CLI
        dependencyTags.Add(WellKnownDependencyTags.AspireCli);

        var steps = new List<StepYaml>();

        // Always: checkout
        steps.Add(new StepYaml
        {
            Name = "Checkout code",
            Uses = "actions/checkout@v4"
        });

        // Conditional: Setup .NET (when any step needs .NET or Aspire CLI, which is .NET-based)
        if (dependencyTags.Contains(WellKnownDependencyTags.DotNet) ||
            dependencyTags.Contains(WellKnownDependencyTags.AspireCli))
        {
            steps.Add(new StepYaml
            {
                Name = "Setup .NET",
                Uses = "actions/setup-dotnet@v4",
                With = new Dictionary<string, string>
                {
                    ["dotnet-version"] = "10.0.x"
                }
            });
        }

        // Conditional: Setup Node.js (when any step needs Node.js)
        if (dependencyTags.Contains(WellKnownDependencyTags.NodeJs))
        {
            steps.Add(new StepYaml
            {
                Name = "Setup Node.js",
                Uses = "actions/setup-node@v4",
                With = new Dictionary<string, string>
                {
                    ["node-version"] = "20"
                }
            });
        }

        // Conditional: Install Aspire CLI (when any step needs it — always true since aspire do runs)
        if (dependencyTags.Contains(WellKnownDependencyTags.AspireCli))
        {
            steps.Add(GenerateAspireCliInstallStep(channel));
        }

        // Conditional: Azure login (when any step needs Azure CLI)
        if (dependencyTags.Contains(WellKnownDependencyTags.AzureCli))
        {
            steps.Add(new StepYaml
            {
                Name = "Azure login",
                Uses = "azure/login@v2",
                With = new Dictionary<string, string>
                {
                    ["client-id"] = "${{ vars.AZURE_CLIENT_ID }}",
                    ["tenant-id"] = "${{ vars.AZURE_TENANT_ID }}",
                    ["subscription-id"] = "${{ vars.AZURE_SUBSCRIPTION_ID }}"
                }
            });
        }

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
                        ["path"] = StatePathExpression
                    }
                });
            }
        }

        // Run aspire do targeting the synthetic scheduling step for this job.
        // The synthetic step depends on the terminal steps, so its transitive closure
        // covers all steps assigned to this job.
        var stageName = FindStageName(workflow, job);
        var syntheticStepName = $"gha-{workflow.Name}-{stageName}-stage-{job.Id}-job";

        steps.Add(new StepYaml
        {
            Name = "Run pipeline steps",
            Run = $"aspire do {syntheticStepName}",
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
                ["path"] = StatePathExpression,
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

    private const string InstallScriptUrl = "https://aspire.dev/install.sh";
    private const string PrInstallScriptUrl = "https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh";

    private static StepYaml GenerateAspireCliInstallStep(string? channel)
    {
        const string addToPath = """echo "$HOME/.aspire/bin" >> $GITHUB_PATH""";

        // Check for PR channel: "pr-<number>" — use the PR-specific install script
        if (channel is not null &&
            channel.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(channel.AsSpan(3), out var prNumber))
        {
            return new StepYaml
            {
                Name = "Install Aspire CLI",
                Run = $"curl -sSL {PrInstallScriptUrl} | bash -s -- {prNumber}\n{addToPath}",
                Env = new Dictionary<string, string>
                {
                    ["GH_TOKEN"] = "${{ github.token }}"
                }
            };
        }

        // Standard channel install via aspire.dev/install.sh with quality flag:
        //   "daily"   → -q dev
        //   "staging" → -q staging
        //   anything else (stable/default/null) → no flag
        var installCommand = channel?.ToLowerInvariant() switch
        {
            "daily" => $"curl -sSL {InstallScriptUrl} | bash -s -- -q dev",
            "staging" => $"curl -sSL {InstallScriptUrl} | bash -s -- -q staging",
            _ => $"curl -sSL {InstallScriptUrl} | bash"
        };

        return new StepYaml
        {
            Name = "Install Aspire CLI",
            Run = $"{installCommand}\n{addToPath}"
        };
    }

    private static string? ReadChannelFromConfig(string? repositoryRootDirectory)
    {
        if (string.IsNullOrEmpty(repositoryRootDirectory))
        {
            return null;
        }

        var configPath = Path.Combine(repositoryRootDirectory, "aspire.config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("channel", out var channelProp) &&
                channelProp.ValueKind == JsonValueKind.String)
            {
                return channelProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed config — fall back to default
        }

        return null;
    }

    private static string FindStageName(GitHubActionsWorkflowResource workflow, GitHubActionsJobResource job)
    {
        foreach (var stage in workflow.Stages)
        {
            for (var i = 0; i < stage.Jobs.Count; i++)
            {
                if (stage.Jobs[i].Id == job.Id)
                {
                    return stage.Name;
                }
            }
        }

        return "default";
    }
}
