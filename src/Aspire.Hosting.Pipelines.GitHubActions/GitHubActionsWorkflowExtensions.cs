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

        resource.Annotations.Add(new PipelineWorkflowGeneratorAnnotation(async context =>
        {
            var workflow = (GitHubActionsWorkflowResource)context.Environment;
            var logger = context.StepContext.Logger;

            // Resolve scheduling (which steps run in which jobs)
            var scheduling = SchedulingResolver.Resolve(context.Steps.ToList(), workflow);

            // Generate the YAML model
            var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);

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
}
