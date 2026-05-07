// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that grounds an agent's responses using data from an Azure AI Search index.
/// </summary>
/// <remarks>
/// After creating this tool with <see cref="PromptAgentBuilderExtensions.AddAISearchTool"/>,
/// link it to an <see cref="AzureSearchResource"/> using
/// <see cref="PromptAgentBuilderExtensions.WithReference(IResourceBuilder{AzureAISearchToolResource}, IResourceBuilder{AzureSearchResource})"/>.
/// The connection identifier is resolved at deploy time when the agent definition is created.
/// </remarks>
[AspireExport]
public class AzureAISearchToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="AzureAISearchToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    public AzureAISearchToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project)
        : base(name, project)
    {
    }

    /// <summary>
    /// Gets or sets the Azure AI Search resource backing this tool.
    /// Set by <see cref="PromptAgentBuilderExtensions.WithReference(IResourceBuilder{AzureAISearchToolResource}, IResourceBuilder{AzureSearchResource})"/>.
    /// </summary>
    public AzureSearchResource? SearchResource { get; internal set; }

    /// <summary>
    /// Gets or sets the optional search index name to query. If not set, the tool
    /// will use a default or prompt-specified index at runtime.
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// Gets or sets the Foundry project connection resource for this search tool.
    /// Set by <see cref="PromptAgentBuilderExtensions.WithReference(IResourceBuilder{AzureAISearchToolResource}, IResourceBuilder{AzureSearchResource})"/>.
    /// </summary>
    internal AzureCognitiveServicesProjectConnectionResource? Connection { get; set; }

    /// <inheritdoc/>
    public override async Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        if (Connection is null)
        {
            throw new InvalidOperationException(
                $"Azure AI Search tool '{Name}' does not have a backing resource configured. " +
                "Call .WithReference(searchResource) to link it to an Azure AI Search resource.");
        }

        // The connection ID output is resolved after infrastructure provisioning
        var connectionIdRef = new BicepOutputReference("id", Connection);
        var connectionId = await connectionIdRef.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connectionId))
        {
            throw new InvalidOperationException(
                $"Failed to resolve connection ID for Azure AI Search tool '{Name}'. " +
                "The Foundry project connection may not have been provisioned correctly.");
        }

        var index = new AzureAISearchToolIndex
        {
            ProjectConnectionId = connectionId,
            IndexName = IndexName
        };
        var options = new AzureAISearchToolOptions([index]);
        return new AzureAISearchTool(options);
    }
}
