// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.AI.Projects.OpenAI;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that grounds an agent's responses using data from an Azure AI Search index.
/// </summary>
/// <remarks>
/// This tool requires an existing <see cref="AzureSearchResource"/> and creates a Foundry project
/// connection to it during provisioning. The connection identifier is resolved at deploy time
/// when the agent definition is created.
/// </remarks>
public class AzureAISearchToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="AzureAISearchToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="searchResource">The Azure AI Search resource to use for grounding.</param>
    public AzureAISearchToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project,
        AzureSearchResource searchResource)
        : base(name, project)
    {
        ArgumentNullException.ThrowIfNull(searchResource);
        SearchResource = searchResource;
    }

    /// <summary>
    /// Gets the Azure AI Search resource backing this tool.
    /// </summary>
    public AzureSearchResource SearchResource { get; }

    /// <summary>
    /// Gets or sets the Foundry project connection resource for this search tool.
    /// This is set during <see cref="PromptAgentBuilderExtensions.AddAzureAISearchTool"/>.
    /// </summary>
    internal AzureCognitiveServicesProjectConnectionResource? Connection { get; set; }

    /// <inheritdoc/>
    public override async Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        if (Connection is null)
        {
            throw new InvalidOperationException(
                $"Azure AI Search tool '{Name}' does not have a project connection. " +
                "Ensure the tool was added using AddAzureAISearchTool().");
        }

        // The connection name output is resolved after infrastructure provisioning
        var connectionNameRef = new BicepOutputReference("name", Connection);
        var connectionName = await connectionNameRef.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connectionName))
        {
            throw new InvalidOperationException(
                $"Failed to resolve connection name for Azure AI Search tool '{Name}'. " +
                "The Foundry project connection may not have been provisioned correctly.");
        }

        var index = new AzureAISearchToolIndex
        {
            ProjectConnectionId = connectionName
        };
        var options = new AzureAISearchToolOptions([index]);
        return new AzureAISearchAgentTool(options);
    }
}
