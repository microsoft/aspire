// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel;
using System.Collections.Immutable;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Microsoft Foundry prompt agent resource that is provisioned on Azure.
/// </summary>
/// <remarks>
/// Unlike hosted agents (which run as containers), prompt agents are configuration-only
/// agents defined by a model, system instructions, and optional tools. They are always
/// deployed to Azure Foundry via the data plane API, even during local development
/// (<c>aspire run</c>). Local services communicate with the cloud-provisioned agent
/// using the Foundry project endpoint and agent name.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class AzurePromptAgentResource : Resource, IResourceWithEnvironment, IResourceWithConnectionString
{
    private const string BeforeStartStepName = "before-start";
    private const string RunModeAzureProvisionStepName = "run-mode-azure-provision";
    private const int ProjectEndpointReadinessMaxRetryAttempts = 11;
    private static readonly TimeSpan s_projectEndpointReadinessDelay = TimeSpan.FromSeconds(5);
    private readonly List<IFoundryTool> _tools = [];

    /// <summary>
    /// Creates a new instance of the <see cref="AzurePromptAgentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the agent. This will also be used as the agent name in Foundry.</param>
    /// <param name="model">The model deployment name to use for this agent.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="instructions">Optional system instructions for the agent.</param>
    public AzurePromptAgentResource(
        [ResourceName] string name,
        string model,
        AzureCognitiveServicesProjectResource project,
        string? instructions = null)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(project);

        Model = model;
        Project = project;
        Instructions = instructions;

        Annotations.Add(new ManifestPublishingCallbackAnnotation(PublishAsync));

        // Set up pipeline steps for deploying this prompt agent
        Annotations.Add(new PipelineStepAnnotation(context =>
        {
            var steps = new List<PipelineStep>();

            if (context.PipelineContext.ExecutionContext.IsRunMode)
            {
                var beforeStartDeployStep = new PipelineStep
                {
                    Name = $"deploy-{Name}-before-start",
                    Description = $"Deploys prompt agent {Name} before the application starts.",
                    Action = DeployBeforeStartAsync,
                    RequiredBySteps = [BeforeStartStepName],
                    Resource = this,
                    DependsOnSteps = [RunModeAzureProvisionStepName]
                };
                steps.Add(beforeStartDeployStep);
            }

            var agentDeployStep = new PipelineStep
            {
                Name = $"deploy-{Name}",
                Description = $"Deploys prompt agent {Name}.",
                Action = async (stepCtx) =>
                {
                    var version = await DeployAsync(Project, stepCtx, logRetry: null, stepCtx.CancellationToken).ConfigureAwait(false);
                    stepCtx.ReportingStep.Log(LogLevel.Information,
                        new MarkdownString($"Successfully deployed **{Name}** as Prompt Agent (version {version.Version})"));
                    Version.Set(version.Version);
                },
                Tags = [WellKnownPipelineTags.DeployCompute],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this,
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq, AzureEnvironmentResource.ProvisionInfrastructureStepName]
            };
            steps.Add(agentDeployStep);

            return Task.FromResult<IEnumerable<PipelineStep>>(steps);
        }));
    }

    /// <summary>
    /// Gets or sets the model deployment name used by this agent.
    /// </summary>
    public string Model { get; set; }

    /// <summary>
    /// Gets the parent Foundry project resource.
    /// </summary>
    public AzureCognitiveServicesProjectResource Project { get; }

    /// <summary>
    /// Gets or sets the system instructions for the agent.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets a description of the agent.
    /// </summary>
    public string Description { get; set; } = "Prompt Agent";

    /// <summary>
    /// Gets the metadata to associate with the agent.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>()
    {
        { "DeployedBy", "Aspire Hosting Framework" },
        { "DeployedOn", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
    };

    /// <summary>
    /// Once deployed, the version that is assigned to this prompt agent.
    /// </summary>
    public StaticValueProvider<string> Version { get; } = new();

    /// <summary>
    /// Gets the list of tool resources attached to this agent.
    /// </summary>
    [AspireExportIgnore(Reason = "IFoundryTool is a .NET extensibility point and is not ATS-compatible.")]
    public IReadOnlyList<IFoundryTool> Tools => _tools;

    /// <summary>
    /// Adds a tool resource to this prompt agent.
    /// </summary>
    /// <param name="tool">The tool resource to add.</param>
    internal void AddTool(FoundryToolResource tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Project != Project)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' belongs to project '{tool.Project.Name}' but agent '{Name}' " +
                $"belongs to project '{Project.Name}'. All tools must belong to the same project as the agent.");
        }

        _tools.Add(tool);
    }

    /// <inheritdoc/>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{Project.Endpoint}/agents/{Name}");

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("AgentName", ReferenceExpression.Create($"{Name}"));
        yield return new("ProjectEndpoint", ReferenceExpression.Create($"{Project.Endpoint}"));
        yield return new("ConnectionString", ConnectionStringExpression);
    }

    /// <summary>
    /// Publishes the prompt agent during the manifest publishing phase.
    /// </summary>
    private Task PublishAsync(ManifestPublishingContext ctx)
    {
        ctx.Writer.WriteString("type", "azure.ai.agent.v0");
        ctx.Writer.WriteStartObject("definition");
        ctx.Writer.WriteString("kind", "prompt");
        ctx.Writer.WriteString("model", Model);
        if (Instructions is not null)
        {
            ctx.Writer.WriteString("instructions", Instructions);
        }
        ctx.Writer.WriteEndObject(); // definition

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deploys the prompt agent to the given Microsoft Foundry project.
    /// </summary>
    private async Task<ProjectsAgentVersion> DeployAsync(
        AzureCognitiveServicesProjectResource project,
        PipelineStepContext context,
        Action<string>? logRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var projectEndpoint = await project.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(projectEndpoint))
        {
            throw new InvalidOperationException($"Project '{project.Name}' does not have a valid endpoint.");
        }

        var options = await ToProjectsAgentVersionCreationOptionsAsync(cancellationToken).ConfigureAwait(false);
        var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());

        var retryPipeline = new ResiliencePipelineBuilder<ProjectsAgentVersion>()
            .AddRetry(new RetryStrategyOptions<ProjectsAgentVersion>
            {
                Delay = s_projectEndpointReadinessDelay,
                MaxRetryAttempts = ProjectEndpointReadinessMaxRetryAttempts,
                ShouldHandle = new PredicateBuilder<ProjectsAgentVersion>()
                    .Handle<ClientResultException>(IsProjectEndpointNotReady),
                OnRetry = retry =>
                {
                    var retryMessage = $"Foundry project endpoint for '{project.Name}' is not ready yet. Retrying prompt agent deployment in {s_projectEndpointReadinessDelay.TotalSeconds:n0} seconds ({retry.AttemptNumber + 1}/{ProjectEndpointReadinessMaxRetryAttempts}).";
                    if (logRetry is not null)
                    {
                        logRetry?.Invoke(retryMessage);
                    }
                    else
                    {
                        context.ReportingStep.Log(LogLevel.Warning, retryMessage);
                    }

                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        return await retryPipeline.ExecuteAsync(async ct =>
        {
            var result = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
                Name,
                options,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return result.Value;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the agent configuration, resolving all tool definitions at deploy time.
    /// </summary>
    private async Task<ProjectsAgentVersionCreationOptions> ToProjectsAgentVersionCreationOptionsAsync(CancellationToken cancellationToken)
    {
        var definition = new DeclarativeAgentDefinition(Model)
        {
            Instructions = Instructions
        };

        foreach (var tool in _tools)
        {
            var agentTool = await tool.ToAgentToolAsync(cancellationToken).ConfigureAwait(false);
            definition.Tools.Add(agentTool);
        }

        var options = new ProjectsAgentVersionCreationOptions(definition)
        {
            Description = Description,
        };

        foreach (var kvp in Metadata)
        {
            options.Metadata[kvp.Key] = kvp.Value;
        }

        return options;
    }

    internal void AddCustomTool(IFoundryTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        _tools.Add(tool);
    }

    private static bool IsProjectEndpointNotReady(ClientResultException ex) =>
        ex.Status == 404 &&
        (ex.Message.Contains("Subdomain does not map to a resource", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("The project does not exist", StringComparison.OrdinalIgnoreCase));

    private Task DeployBeforeStartAsync(PipelineStepContext context)
    {
        if (!context.ExecutionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        StartRunModeDeployment(context);

        return Task.CompletedTask;
    }

    private void StartRunModeDeployment(PipelineStepContext context)
    {
        var lifetime = context.Services.GetRequiredService<IHostApplicationLifetime>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);

        _ = Task.Run(async () =>
        {
            try
            {
                await DeployForRunModeAsync(context, linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task DeployForRunModeAsync(
        PipelineStepContext context,
        CancellationToken cancellationToken)
    {
        var notificationService = context.Services.GetRequiredService<ResourceNotificationService>();
        var logger = context.Services.GetRequiredService<ResourceLoggerService>().GetLogger(this);
        try
        {
            foreach (var tool in Tools.OfType<FoundryToolResource>())
            {
                await notificationService.PublishUpdateAsync(tool, s => s with
                {
                    State = new("Waiting", KnownResourceStateStyles.Info)
                }).ConfigureAwait(false);
            }

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Waiting for project", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            await WaitForProjectAndToolsAsync(notificationService, cancellationToken).ConfigureAwait(false);

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Deploying agent", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            logger.LogInformation("Deploying prompt agent '{AgentName}' to Foundry project '{ProjectName}'...", Name, Project.Name);

            var version = await DeployAsync(Project, context, message => logger.LogWarning("{Message}", message), cancellationToken).ConfigureAwait(false);
            Version.Set(version.Version);

            logger.LogInformation("Successfully deployed prompt agent '{AgentName}' (version {Version})", Name, version.Version);

            foreach (var tool in Tools.OfType<FoundryToolResource>())
            {
                await notificationService.PublishUpdateAsync(tool, s => s with
                {
                    State = new("Running", KnownResourceStateStyles.Success)
                }).ConfigureAwait(false);
            }

            var configuration = context.Services.GetRequiredService<IConfiguration>();
            var portalUrls = await BuildPortalUrlsAsync(configuration, cancellationToken).ConfigureAwait(false);

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Running", KnownResourceStateStyles.Success),
                Urls = portalUrls
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deploy prompt agent '{AgentName}'", Name);

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Failed to deploy", KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }
    }

    private async Task WaitForProjectAndToolsAsync(ResourceNotificationService notificationService, CancellationToken cancellationToken)
    {
        if (Project is IAzureResource { ProvisioningTaskCompletionSource: { } projectProvisioning })
        {
            await projectProvisioning.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await notificationService.WaitForResourceAsync(
                Project.Name,
                KnownResourceStates.Running,
                cancellationToken).ConfigureAwait(false);
        }

        var toolConnectionProvisioningTasks = Tools
            .Select(tool => tool switch
            {
                BingGroundingToolResource { Connection: IAzureResource bingConnection } => bingConnection.ProvisioningTaskCompletionSource?.Task,
                AzureAISearchToolResource { Connection: IAzureResource searchConnection } => searchConnection.ProvisioningTaskCompletionSource?.Task,
                _ => null
            })
            .OfType<Task>();

        await Task.WhenAll(toolConnectionProvisioningTasks.Select(task => task.WaitAsync(cancellationToken))).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<UrlSnapshot>> BuildPortalUrlsAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var subscriptionId = configuration["Azure:SubscriptionId"];
        var resourceGroupName = configuration["Azure:ResourceGroup"];
        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceGroupName))
        {
            return [];
        }

        var foundryAccountName = await Project.Parent.NameOutputReference.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var projectNameOutput = await Project.NameOutputReference.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(foundryAccountName) || string.IsNullOrEmpty(projectNameOutput))
        {
            return [];
        }

        var projectName = projectNameOutput.Contains('/')
            ? projectNameOutput[(projectNameOutput.LastIndexOf('/') + 1)..]
            : projectNameOutput;
        var encodedSubscriptionId = AzureCognitiveServicesProjectResource.EncodeSubscriptionId(subscriptionId);
        var portalUrl = $"https://ai.azure.com/nextgen/r/{encodedSubscriptionId},{resourceGroupName},,{foundryAccountName},{projectName}/build/agents/{Name}/build";

        return [new UrlSnapshot(Name: "Foundry Portal", Url: portalUrl, IsInternal: false)];
    }
}
