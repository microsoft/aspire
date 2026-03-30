// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines.GitHubActions.Yaml;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Extension methods for adding GitHub Actions workflow resources to a distributed application.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class GitHubActionsWorkflowExtensions
{
    /// <summary>
    /// Adds a GitHub Actions workflow resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the workflow resource. This also becomes the workflow filename (e.g., "deploy" → "deploy.yml").</param>
    /// <returns>A resource builder for the workflow resource.</returns>
    [AspireExportIgnore(Reason = "Pipeline generation is not yet ATS-compatible")]
    public static IResourceBuilder<GitHubActionsWorkflowResource> AddGitHubActionsWorkflow(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new GitHubActionsWorkflowResource(name);

        resource.Annotations.Add(new PipelineEnvironmentCheckAnnotation(context =>
        {
            // This environment is relevant when running inside GitHub Actions
            var isGitHubActions = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
            return Task.FromResult(isGitHubActions);
        }));

        resource.Annotations.Add(new PipelineScopeAnnotation(context =>
        {
            var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
            var runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "1";
            var jobId = Environment.GetEnvironmentVariable("GITHUB_JOB");

            if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(jobId))
            {
                return Task.FromResult<PipelineScopeResult?>(null);
            }

            return Task.FromResult<PipelineScopeResult?>(new PipelineScopeResult
            {
                RunId = $"{runId}-{runAttempt}",
                JobId = jobId
            });
        }));

        resource.Annotations.Add(new PipelineStepAnnotation(context =>
        {
            var workflow = (GitHubActionsWorkflowResource)context.Resource;
            var existingSteps = context.ExistingSteps;

            if (existingSteps.Count == 0)
            {
                return [];
            }

            // Filter out orphaned steps that have no connection to the deployment graph.
            // Steps like "publish-manifest", "diagnostics", and "pipeline-init" are
            // standalone tools for the CLI, not part of the deployment DAG.
            var connectedSteps = FilterConnectedSteps(existingSteps);

            if (connectedSteps.Count == 0)
            {
                return [];
            }

            // Run the scheduler to compute job assignments and terminal steps
            var scheduling = SchedulingResolver.Resolve(connectedSteps, workflow);

            // Register scope map so the executor can filter steps in continuation mode.
            // This must happen here (not in the generator callback) so it's available
            // during both `aspire pipeline init` and `aspire do` executions.
            var scopeToSteps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            // Create a synthetic step per job that depends on the terminal steps for that job
            var syntheticSteps = new List<PipelineStep>();
            foreach (var job in workflow.Jobs)
            {
                var stageName = FindStageName(workflow, job);
                var stepName = $"gha-{workflow.Name}-{stageName}-stage-{job.Id}-job";

                var terminalSteps = scheduling.TerminalStepsPerJob.GetValueOrDefault(job.Id);

                var syntheticStep = new PipelineStep
                {
                    Name = stepName,
                    Description = $"Scheduling target for job '{job.Id}' in workflow '{workflow.Name}'",
                    Action = _ => Task.CompletedTask,
                    DependsOnSteps = terminalSteps?.ToList() ?? [],
                    ScheduledBy = job
                };

                syntheticSteps.Add(syntheticStep);

                // Build scope map entry: include all real steps + the synthetic step
                var jobStepNames = scheduling.StepsPerJob.TryGetValue(job.Id, out var jobSteps)
                    ? jobSteps.Select(s => s.Name).ToList()
                    : [];
                jobStepNames.Add(stepName);
                scopeToSteps[job.Id] = jobStepNames;
            }

            workflow.Annotations.Add(new PipelineScopeMapAnnotation(scopeToSteps));

            return syntheticSteps;
        }));

        resource.Annotations.Add(new PipelineWorkflowGeneratorAnnotation(async context =>
        {
            var workflow = (GitHubActionsWorkflowResource)context.Environment;
            var logger = context.StepContext.Logger;

            // Resolve scheduling (which steps run in which jobs).
            // Filter out orphaned steps (same as the annotation factory) so the YAML
            // generator only includes steps that are part of the deployment graph.
            // Note: scope map is already registered by the PipelineStepAnnotation factory above.
            var connectedSteps = FilterConnectedSteps(context.Steps);
            var scheduling = SchedulingResolver.Resolve(connectedSteps, workflow);

            // Generate the YAML model
            var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);

            // Apply user customization callbacks
            foreach (var customization in workflow.Annotations.OfType<WorkflowCustomizationAnnotation>())
            {
                customization.Callback(yamlModel);
            }

            // Serialize to YAML string
            var yamlContent = WorkflowYamlSerializer.Serialize(yamlModel);

            // Write to .github/workflows/{name}.yml relative to the repo root
            var outputDir = Path.Combine(context.RepositoryRootDirectory, ".github", "workflows");
            Directory.CreateDirectory(outputDir);

            var outputPath = Path.Combine(outputDir, workflow.WorkflowFileName);
            await File.WriteAllTextAsync(outputPath, yamlContent, context.CancellationToken).ConfigureAwait(false);

            logger.LogInformation("Generated GitHub Actions workflow: {Path}", outputPath);
            context.StepContext.Summary.Add("📄 Workflow", outputPath);
        }));

        return builder.AddResource(resource)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Adds a stage to the GitHub Actions workflow. Stages are a logical grouping of jobs.
    /// </summary>
    /// <param name="builder">The workflow resource builder.</param>
    /// <param name="name">The unique stage name within the workflow.</param>
    /// <returns>The created <see cref="GitHubActionsStageResource"/>.</returns>
    [AspireExportIgnore(Reason = "Pipeline generation is not yet ATS-compatible")]
    public static GitHubActionsStageResource AddStage(
        this IResourceBuilder<GitHubActionsWorkflowResource> builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return builder.Resource.AddStage(name);
    }

    /// <summary>
    /// Adds a job to the GitHub Actions workflow.
    /// </summary>
    /// <param name="builder">The workflow resource builder.</param>
    /// <param name="id">The unique job identifier within the workflow.</param>
    /// <returns>The created <see cref="GitHubActionsJobResource"/>.</returns>
    [AspireExportIgnore(Reason = "Pipeline generation is not yet ATS-compatible")]
    public static GitHubActionsJobResource AddJob(
        this IResourceBuilder<GitHubActionsWorkflowResource> builder,
        string id)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(id);

        return builder.Resource.AddJob(id);
    }

    /// <summary>
    /// Registers a callback to customize the generated <see cref="Yaml.WorkflowYaml"/> model
    /// before it is serialized to disk. Multiple callbacks can be registered and will be
    /// invoked in registration order.
    /// </summary>
    /// <param name="builder">The workflow resource builder.</param>
    /// <param name="configure">A callback that receives the <see cref="Yaml.WorkflowYaml"/> model for mutation.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <example>
    /// <code>
    /// var workflow = builder.AddGitHubActionsWorkflow("deploy");
    /// workflow.ConfigureWorkflow(yaml =&gt;
    /// {
    ///     foreach (var job in yaml.Jobs.Values)
    ///     {
    ///         job.Env ??= new();
    ///         job.Env["MY_SECRET"] = "${{ secrets.MY_SECRET }}";
    ///     }
    /// });
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Pipeline generation is not yet ATS-compatible")]
    public static IResourceBuilder<GitHubActionsWorkflowResource> ConfigureWorkflow(
        this IResourceBuilder<GitHubActionsWorkflowResource> builder,
        Action<Yaml.WorkflowYaml> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.WithAnnotation(new WorkflowCustomizationAnnotation(configure));
    }

    /// <summary>
    /// Finds the stage name that contains the specified job, or "default" if the job
    /// is not part of any explicit stage.
    /// </summary>
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

    /// <summary>
    /// Filters out orphaned steps that have no connection to the deployment graph.
    /// A step is "connected" if it has dependencies, is depended upon by other steps,
    /// or has explicit scheduling. Steps like "publish-manifest", "diagnostics", and
    /// "pipeline-init" are standalone CLI tools and should not be scheduled into CI jobs.
    /// </summary>
    private static List<PipelineStep> FilterConnectedSteps(IReadOnlyList<PipelineStep> steps)
    {
        // Build set of step names that are depended upon
        var dependedUpon = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var dep in step.DependsOnSteps)
            {
                dependedUpon.Add(dep);
            }
            foreach (var req in step.RequiredBySteps)
            {
                dependedUpon.Add(req);
            }
        }

        var connected = new List<PipelineStep>();
        foreach (var step in steps)
        {
            // A step is connected if:
            // - It depends on other steps
            // - Other steps depend on it (via DependsOn or RequiredBy)
            // - It has explicit scheduling (ScheduledBy is set)
            if (step.DependsOnSteps.Count > 0 ||
                step.RequiredBySteps.Count > 0 ||
                dependedUpon.Contains(step.Name) ||
                step.ScheduledBy is not null)
            {
                connected.Add(step);
            }
        }

        return connected;
    }
}
