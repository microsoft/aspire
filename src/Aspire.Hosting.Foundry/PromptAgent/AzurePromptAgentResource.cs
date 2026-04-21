// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Extensions.Logging;

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
public class AzurePromptAgentResource : Resource, IComputeResource, IResourceWithEnvironment, IResourceWithConnectionString
{
    private readonly List<FoundryToolResource> _tools = [];

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
        Annotations.Add(new PipelineStepAnnotation(async (ctx) =>
        {
            var steps = new List<PipelineStep>();

            var agentDeployStep = new PipelineStep
            {
                Name = $"deploy-{Name}",
                Action = async (stepCtx) =>
                {
                    var version = await DeployAsync(stepCtx, Project).ConfigureAwait(false);
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

            return steps;
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
    public IReadOnlyList<FoundryToolResource> Tools => _tools;

    /// <summary>
    /// Gets the list of custom tool implementations (escape hatch for advanced scenarios).
    /// </summary>
    internal List<IFoundryTool> CustomTools { get; } = [];

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
    public async Task PublishAsync(ManifestPublishingContext ctx)
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
    }

    /// <summary>
    /// Deploys the prompt agent to the given Microsoft Foundry project.
    /// </summary>
    public async Task<ProjectsAgentVersion> DeployAsync(PipelineStepContext context, AzureCognitiveServicesProjectResource project)
    {
        return await DeployAsync(project, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deploys the prompt agent to the parent Microsoft Foundry project.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deployed agent version.</returns>
    public async Task<ProjectsAgentVersion> DeployAsync(CancellationToken cancellationToken = default)
    {
        return await DeployAsync(Project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deploys the prompt agent to the given Microsoft Foundry project.
    /// </summary>
    internal async Task<ProjectsAgentVersion> DeployAsync(AzureCognitiveServicesProjectResource project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var projectEndpoint = await project.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(projectEndpoint))
        {
            throw new InvalidOperationException($"Project '{project.Name}' does not have a valid endpoint.");
        }

        var config = await ToPromptAgentConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());
        var result = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
            Name,
            config.ToProjectsAgentVersionCreationOptions(),
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        return result.Value;
    }

    /// <summary>
    /// Builds the agent configuration, resolving all tool definitions at deploy time.
    /// </summary>
    internal async Task<PromptAgentConfiguration> ToPromptAgentConfigurationAsync(CancellationToken cancellationToken)
    {
        var config = new PromptAgentConfiguration(Model, Instructions)
        {
            Description = Description,
            Metadata = new Dictionary<string, string>(Metadata)
        };

        foreach (var tool in _tools)
        {
            var agentTool = await tool.ToAgentToolAsync(null, cancellationToken).ConfigureAwait(false);
            config.Tools.Add(agentTool);
        }

        foreach (var customTool in CustomTools)
        {
            var agentTool = await customTool.ToAgentToolAsync(null, cancellationToken).ConfigureAwait(false);
            config.Tools.Add(agentTool);
        }

        return config;
    }
}
