// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREINTERACTION001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a Docker Compose environment resource that can host application resources.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DockerComposeEnvironmentResource"/> class.
/// </remarks>
[global::Aspire.Hosting.AspireExport(ExposeProperties = true)]
public class DockerComposeEnvironmentResource : Resource, IComputeEnvironmentResource
{
    private const string DockerComposeUpTag = "docker-compose-up";

    /// <summary>
    /// The name of an existing network to be used.
    /// </summary>
    public string? DefaultNetworkName { get; set; }

    /// <summary>
    /// Determines whether to include an Aspire dashboard for telemetry visualization in this environment.
    /// </summary>
    public bool DashboardEnabled { get; set; } = true;

    internal Action<ComposeFile>? ConfigureComposeFile { get; set; }

    internal Action<IDictionary<string, CapturedEnvironmentVariable>>? ConfigureEnvFile { get; set; }

    internal IResourceBuilder<DockerComposeAspireDashboardResource>? Dashboard { get; set; }

    /// <summary>
    /// Gets the collection of environment variables captured from the Docker Compose environment.
    /// These will be populated into a top-level .env file adjacent to the Docker Compose file.
    /// </summary>
    internal Dictionary<string, CapturedEnvironmentVariable> CapturedEnvironmentVariables { get; } = [];

    internal Dictionary<IResource, DockerComposeServiceResource> ResourceMapping { get; } = new(new ResourceNameComparer());

    internal IPortAllocator PortAllocator { get; } = new PortAllocator();

    /// <param name="name">The name of the Docker Compose environment.</param>
    public DockerComposeEnvironmentResource(string name) : base(name)
    {
        Annotations.Add(new PipelineStepAnnotation(async (factoryContext) =>
        {
            var model = factoryContext.PipelineContext.Model;
            var steps = new List<PipelineStep>();

            var publishStep = new PipelineStep
            {
                Name = $"publish-{Name}",
                Description = $"Publishes the Docker Compose environment configuration for {Name}.",
                Action = ctx => PublishAsync(ctx)
            };
            publishStep.RequiredBy(WellKnownPipelineSteps.Publish);
            steps.Add(publishStep);

            // Expand deployment target steps for all compute resources (including dashboard if enabled)
            var resources = DashboardEnabled && Dashboard?.Resource is DockerComposeAspireDashboardResource dashboard
                ? [.. model.GetComputeResources(), dashboard]
                : model.GetComputeResources();

            foreach (var resource in resources)
            {
                var deploymentTarget = resource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

                if (deploymentTarget != null && deploymentTarget.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
                {
                    foreach (var annotation in annotations)
                    {
                        var childFactoryContext = new PipelineStepFactoryContext
                        {
                            PipelineContext = factoryContext.PipelineContext,
                            Resource = deploymentTarget
                        };

                        var deploymentTargetSteps = await annotation.CreateStepsAsync(childFactoryContext).ConfigureAwait(false);

                        foreach (var step in deploymentTargetSteps)
                        {
                            step.Resource ??= deploymentTarget;
                        }

                        steps.AddRange(deploymentTargetSteps);
                    }
                }
            }

            var prepareStep = new PipelineStep
            {
                Name = $"prepare-{Name}",
                Description = $"Prepares the Docker Compose environment {Name} for deployment.",
                Action = ctx => PrepareAsync(ctx)
            };
            prepareStep.DependsOn(WellKnownPipelineSteps.Publish);
            prepareStep.DependsOn(WellKnownPipelineSteps.Build);
            steps.Add(prepareStep);

            var dockerComposeUpStep = new PipelineStep
            {
                Name = $"docker-compose-up-{Name}",
                Action = ctx => DockerComposeUpAsync(ctx),
                Tags = [DockerComposeUpTag],
                DependsOnSteps = [$"prepare-{Name}"]
            };
            dockerComposeUpStep.RequiredBy(WellKnownPipelineSteps.Deploy);
            steps.Add(dockerComposeUpStep);

            var dockerComposeDestroyStep = new PipelineStep
            {
                Name = $"destroy-compose-{Name}",
                Description = $"Confirms and destroys the Docker Compose environment {Name}.",
                Action = async ctx =>
                {
                    // Check deployment state to verify this environment was actually deployed
                    var deploymentStateManager = ctx.Services.GetRequiredService<IDeploymentStateManager>();
                    var stateSection = await deploymentStateManager.AcquireSectionAsync($"DockerCompose:{Name}", ctx.CancellationToken).ConfigureAwait(false);
                    var savedComposeFilePath = stateSection.Data["ComposeFilePath"]?.ToString();

                    if (string.IsNullOrEmpty(savedComposeFilePath))
                    {
                        await ctx.ReportingStep.CompleteAsync(
                            $"No Docker Compose deployment state found for '{Name}'. Nothing to destroy.",
                            CompletionState.Completed,
                            ctx.CancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await ConfirmDestroyAsync(ctx, Name).ConfigureAwait(false);

                    // Use saved state to build the compose context — don't recompute from current model
                    // Only use the project name for down — the compose file may not be valid for down
                    // (e.g., services with build contexts that no longer exist)
                    var savedOutputPath = stateSection.Data["OutputPath"]?.ToString() ?? Path.GetDirectoryName(savedComposeFilePath)!;
                    var savedProjectName = stateSection.Data["ProjectName"]?.ToString() ?? GetDockerComposeProjectName(ctx, this);

                    var runtime = await ctx.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(ctx.CancellationToken).ConfigureAwait(false);

                    var composeContext = new ComposeOperationContext
                    {
                        ProjectName = savedProjectName,
                        WorkingDirectory = savedOutputPath
                    };

                    var deployTask = await ctx.ReportingStep.CreateTaskAsync(
                        new MarkdownString($"Running compose down for **{Name}** using **{runtime.Name}**"),
                        ctx.CancellationToken).ConfigureAwait(false);
                    await using (deployTask.ConfigureAwait(false))
                    {
                        await runtime.ComposeDownAsync(composeContext, ctx.CancellationToken).ConfigureAwait(false);
                        await deployTask.CompleteAsync(
                            new MarkdownString($"Compose shutdown complete for **{Name}** ({runtime.Name})"),
                            CompletionState.Completed,
                            ctx.CancellationToken).ConfigureAwait(false);
                    }

                    ctx.Summary.Add("🗑️ Compose", Name);

                    // Clean up deployment state only after successful teardown
                    await deploymentStateManager.DeleteSectionAsync(stateSection, ctx.CancellationToken).ConfigureAwait(false);
                },
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
            };
            dockerComposeDestroyStep.RequiredBy(WellKnownPipelineSteps.Destroy);
            steps.Add(dockerComposeDestroyStep);

            var dockerComposeDownStep = new PipelineStep
            {
                Name = $"docker-compose-down-{Name}",
                Action = ctx => DockerComposeDownAsync(ctx),
                Tags = ["docker-compose-down"]
            };
            steps.Add(dockerComposeDownStep);

            return steps;
        }));

        // Add pipeline configuration annotation to wire up dependencies
        // This is where we wire up the build steps created by the resources
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            // Wire up build step dependencies for all compute resources (including dashboard if enabled)
            var resources = DashboardEnabled && Dashboard?.Resource is DockerComposeAspireDashboardResource dashboard
                ? [.. context.Model.GetComputeResources(), dashboard]
                : context.Model.GetComputeResources();

            foreach (var resource in resources)
            {
                var deploymentTarget = resource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

                if (deploymentTarget is null)
                {
                    continue;
                }

                // Execute the PipelineConfigurationAnnotation callbacks on the deployment target
                if (deploymentTarget.TryGetAnnotationsOfType<PipelineConfigurationAnnotation>(out var annotations))
                {
                    foreach (var annotation in annotations)
                    {
                        annotation.Callback(context);
                    }
                }

                // Ensure print-summary steps from deployment targets run after docker-compose-up
                var printSummarySteps = context.GetSteps(deploymentTarget, "print-summary");
                var dockerComposeUpSteps = context.GetSteps(this, "docker-compose-up");
                printSummarySteps.DependsOn(dockerComposeUpSteps);
            }

            // This ensures that resources that have to be built before deployments are handled
            foreach (var computeResource in context.Model.GetBuildResources())
            {
                var buildSteps = context.GetSteps(computeResource, WellKnownPipelineTags.BuildCompute);

                buildSteps.RequiredBy(WellKnownPipelineSteps.Deploy)
                        .RequiredBy($"docker-compose-up-{Name}")
                        .DependsOn(WellKnownPipelineSteps.DeployPrereq);
            }

            // This ensures that resources that have to be pushed before deployments are handled
            foreach (var pushResource in context.Model.GetBuildAndPushResources())
            {
                var pushSteps = context.GetSteps(pushResource, WellKnownPipelineTags.PushContainerImage);
                var dockerComposeUpSteps = context.GetSteps(this, DockerComposeUpTag);

                dockerComposeUpSteps.DependsOn(pushSteps);
            }
        }));
    }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;

        // In Docker Compose, services can communicate using their service names
        // Docker Compose automatically creates a network where services can reach each other by service name
        return ReferenceExpression.Create($"{resource.Name.ToLowerInvariant()}");
    }

    private Task PublishAsync(PipelineStepContext context)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, this);
        var imageBuilder = context.Services.GetRequiredService<IResourceContainerImageManager>();

        var dockerComposePublishingContext = new DockerComposePublishingContext(
            context.ExecutionContext,
            imageBuilder,
            outputPath,
            context.Logger,
            context.ReportingStep,
            context.CancellationToken);

        return dockerComposePublishingContext.WriteModelAsync(context.Model, this);
    }

    private async Task DockerComposeUpAsync(PipelineStepContext context)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, this);
        var dockerComposeFilePath = Path.Combine(outputPath, "docker-compose.yaml");

        if (!File.Exists(dockerComposeFilePath))
        {
            throw new InvalidOperationException($"Docker Compose file not found at {dockerComposeFilePath}");
        }

        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken).ConfigureAwait(false);

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Running compose up for **{Name}** using **{runtime.Name}**"),
            context.CancellationToken).ConfigureAwait(false);
        await using (deployTask.ConfigureAwait(false))
        {
            try
            {
                var composeContext = CreateComposeOperationContext(context);

                await runtime.ComposeUpAsync(composeContext, context.CancellationToken).ConfigureAwait(false);

                // Persist deployment state so destroy can find the compose file and project name
                var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
                var stateSection = await deploymentStateManager.AcquireSectionAsync($"DockerCompose:{Name}", context.CancellationToken).ConfigureAwait(false);
                stateSection.Data["OutputPath"] = outputPath;
                stateSection.Data["ProjectName"] = composeContext.ProjectName;
                stateSection.Data["ComposeFilePath"] = composeContext.ComposeFilePath;
                await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);

                await deployTask.CompleteAsync(
                    new MarkdownString($"Service **{Name}** is now running with Docker Compose locally (runtime: {runtime.Name})"),
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await deployTask.CompleteAsync($"Compose deployment failed ({runtime.Name}): {ex.Message}", CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task DockerComposeDownAsync(PipelineStepContext context)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, this);
        var dockerComposeFilePath = Path.Combine(outputPath, "docker-compose.yaml");

        if (!File.Exists(dockerComposeFilePath))
        {
            throw new InvalidOperationException(
                $"Docker Compose file not found at '{dockerComposeFilePath}'. " +
                $"If you deployed with a custom --output-path, pass the same path to the destroy command.");
        }

        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken).ConfigureAwait(false);

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Running compose down for **{Name}** using **{runtime.Name}**"),
            context.CancellationToken).ConfigureAwait(false);
        await using (deployTask.ConfigureAwait(false))
        {
            try
            {
                var composeContext = CreateComposeOperationContext(context);

                await runtime.ComposeDownAsync(composeContext, context.CancellationToken).ConfigureAwait(false);

                await deployTask.CompleteAsync(
                    new MarkdownString($"Compose shutdown complete for **{Name}** ({runtime.Name})"),
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await deployTask.CompleteAsync($"Compose shutdown failed ({runtime.Name}): {ex.Message}", CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task ConfirmDestroyAsync(PipelineStepContext context, string environmentName)
    {
        var options = context.Services.GetRequiredService<IOptions<PipelineOptions>>();

        if (!options.Value.SkipConfirmation)
        {
            var interactionService = context.Services.GetRequiredService<IInteractionService>();

            if (!interactionService.IsAvailable)
            {
                throw new InvalidOperationException(
                    "Cannot perform destructive operation without confirmation. Use --yes to skip the confirmation prompt in non-interactive mode.");
            }

            var result = await interactionService.PromptNotificationAsync(
                "Destroy environment",
                $"Shut down Docker Compose environment '{environmentName}'? This will stop and remove all containers, networks, and volumes.",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    ShowSecondaryButton = true,
                    ShowDismiss = false,
                    PrimaryButtonText = "Destroy",
                    SecondaryButtonText = "Cancel"
                },
                context.CancellationToken).ConfigureAwait(false);

            if (result.Canceled || !result.Data)
            {
                context.Logger.LogInformation("User canceled the destroy operation.");
                throw new OperationCanceledException("Destroy operation canceled by user.");
            }
        }
    }

    private async Task PrepareAsync(PipelineStepContext context)
    {
        var envFilePath = GetEnvFilePath(context, this);

        if (CapturedEnvironmentVariables.Count == 0)
        {
            return;
        }

        // Initialize a new EnvFile for this environment
        var envFile = EnvFile.Create(envFilePath, context.Logger);

        foreach (var entry in CapturedEnvironmentVariables)
        {
            var envVar = entry.Value;
            var defaultValue = envVar.DefaultValue;

            if (defaultValue is null && envVar.Source is ParameterResource parameter)
            {
                defaultValue = await parameter.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            }

            if (envVar.Source is ContainerImageReference cir)
            {
                defaultValue = await ((IValueProvider)cir).GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            }

            envFile.Add(entry.Key, defaultValue, envVar.Description, onlyIfMissing: false);
        }

        envFile.Save(includeValues: true);
    }

    internal string AddEnvironmentVariable(string name, string? description = null, string? defaultValue = null, object? source = null, IResource? resource = null)
    {
        CapturedEnvironmentVariables[name] = new CapturedEnvironmentVariable
        {
            Name = name,
            Description = description,
            DefaultValue = defaultValue,
            Source = source,
            Resource = resource
        };

        return $"${{{name}}}";
    }

    internal static string GetEnvFilePath(PipelineStepContext context, DockerComposeEnvironmentResource environment)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, environment);
        var hostEnvironment = context.Services.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        var environmentName = hostEnvironment?.EnvironmentName ?? environment.Name;
        var envFilePath = Path.Combine(outputPath, $".env.{environmentName}");
        return envFilePath;
    }

    internal ComposeOperationContext CreateComposeOperationContext(PipelineStepContext context)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, this);
        return new ComposeOperationContext
        {
            ComposeFilePath = Path.Combine(outputPath, "docker-compose.yaml"),
            ProjectName = GetDockerComposeProjectName(context, this),
            EnvFilePath = GetEnvFilePath(context, this),
            WorkingDirectory = outputPath
        };
    }

    internal static string GetDockerComposeProjectName(PipelineStepContext context, DockerComposeEnvironmentResource environment)
    {
        // Get the AppHost:PathSha256 from configuration to disambiguate projects
        var configuration = context.Services.GetService<IConfiguration>();
        var appHostSha = configuration?["AppHost:PathSha256"];

        if (!string.IsNullOrEmpty(appHostSha) && appHostSha.Length >= 8)
        {
            // Use first 8 characters of the hash for readability
            // Format: aspire-{environmentName}-{sha8}
            return $"aspire-{environment.Name.ToLowerInvariant()}-{appHostSha[..8].ToLowerInvariant()}";
        }

        // Fallback to just using the environment name if PathSha256 is not available
        return $"aspire-{environment.Name.ToLowerInvariant()}";
    }
}
