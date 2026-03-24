// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Microsoft Foundry prompt agent resource.
/// </summary>
public class AzurePromptAgentResource : ExecutableResource, IComputeResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="AzurePromptAgentResource"/> class.
    /// </summary>
    public AzurePromptAgentResource([ResourceName] string name, string model, string? instructions) : base(name, "python", "")
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(instructions);
        Model = model;
        Instructions = instructions;
        Annotations.Add(new ManifestPublishingCallbackAnnotation(PublishAsync));
        // Set up steps for deploying this particular hosted agent
        Annotations.Add(new PipelineStepAnnotation(async (ctx) =>
        {
            List<PipelineStep> steps = [];
            PipelineStep? pushStep = null;
            var deploymentAnnotation = this.GetDeploymentTargetAnnotation() ?? throw new InvalidOperationException($"Deployment target annotation is required on resource '{this.Name}' to deploy as hosted agent.");
            var project = deploymentAnnotation.ComputeEnvironment as AzureCognitiveServicesProjectResource
                ?? throw new InvalidOperationException($"Compute environment for resource '{this.Name}' must be an AzureCognitiveServicesProjectResource to deploy as hosted agent.");
            // Create a step to deploy
            var agentDeployStep = new PipelineStep
            {
                Name = $"deploy-{Name}",
                Action = async (ctx) =>
                {
                    var version = await DeployAsync(ctx, project).ConfigureAwait(false);
#pragma warning disable CS0618
                    ctx.ReportingStep.Log(LogLevel.Information, $"Successfully deployed **{Name}** as Prompt Agent (version {version})", enableMarkdown: true);
#pragma warning restore CS0618
                    Version.Set(version.Version);
                },
                Tags = [WellKnownPipelineTags.DeployCompute],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this,
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq, AzureEnvironmentResource.ProvisionInfrastructureStepName]
            };
            steps.Add(agentDeployStep);
            if (pushStep is not null)
            {
                agentDeployStep.DependsOn(pushStep);
            }
            return steps;
        }));
    }

    /// <summary>
    /// The model to use for the prompt agent. This should generally correspond to the Deployment Name.
    /// </summary>
    public string Model { get; set; }

    /// <summary>
    /// The "system prompt" instructions for the prompt agent.
    /// </summary>
    public string Instructions { get; set; }

    /// <summary>
    /// The internal description of the prompt agent.
    /// </summary>
    public string Description { get; set; } = "Prompt Agent";

    /// <summary>
    /// Additional metadata to associate with the agent.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>()
    {
        { "DeployedBy", "Aspire Hosting Framework" },
        { "DeployedOn", DateTime.UtcNow.ToString("o") }
    };

    /// <summary>
    /// Once deployed, the version that is assigned to this agent.
    /// </summary>
    public StaticValueProvider<string> Version { get; } = new();

    /// <summary>
    /// Publishes the agent during the manifest publishing phase.
    /// </summary>
    public async Task PublishAsync(ManifestPublishingContext ctx)
    {
        // Write agent manifest
        ctx.Writer.WriteString("type", "azure.ai.agent.v0");
        ctx.Writer.WriteStartObject("definition");
        ctx.Writer.WriteString("kind", "prompt");
        ctx.Writer.WriteString("model", Model);
        ctx.Writer.WriteString("instructions", Instructions);
        ctx.Writer.WriteEndObject(); // definition
    }

    /// <summary>
    /// Deploys the specified agent to the given Microsoft Foundry project.
    /// </summary>
    public async Task<AgentVersion> DeployAsync(PipelineStepContext context, AzureCognitiveServicesProjectResource project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var projectEndpoint = await project.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(projectEndpoint))
        {
            throw new InvalidOperationException($"Project '{project.Name}' does not have a valid connection string.");
        }
        var options = new AgentVersionCreationOptions(new PromptAgentDefinition(Model)
        {
            Instructions = Instructions ?? "",
        })
        {
            Description = Description,
        };
        foreach (var kvp in Metadata)
        {
            options.Metadata[kvp.Key] = kvp.Value;
        }
        var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());
        var result = await projectClient.Agents.CreateAgentVersionAsync(
            Name,
            options,
            context.CancellationToken
        ).ConfigureAwait(false);
        return result.Value;
    }
}
