// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.AI.Projects.OpenAI;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that grounds an agent's responses using Bing Search.
/// </summary>
/// <remarks>
/// This tool requires a Bing Search connection configured in the Foundry project.
/// The Bing connection is provisioned through Foundry infrastructure, not as a
/// standalone Aspire resource.
/// </remarks>
public class BingGroundingToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="BingGroundingToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    public BingGroundingToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project)
        : base(name, project)
    {
    }

    /// <summary>
    /// Gets or sets the Foundry project connection resource for the Bing Search service.
    /// </summary>
    internal AzureCognitiveServicesProjectConnectionResource? Connection { get; set; }

    /// <inheritdoc/>
    public override async Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        if (Connection is null)
        {
            throw new InvalidOperationException(
                $"Bing Grounding tool '{Name}' does not have a project connection. " +
                "Ensure the tool was added using AddBingGroundingTool().");
        }

        var connectionNameRef = new BicepOutputReference("name", Connection);
        var connectionName = await connectionNameRef.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connectionName))
        {
            throw new InvalidOperationException(
                $"Failed to resolve connection name for Bing Grounding tool '{Name}'. " +
                "The Foundry project connection may not have been provisioned correctly.");
        }

        var config = new BingGroundingSearchConfiguration(connectionName);
        var options = new BingGroundingSearchToolOptions([config]);
        return new BingGroundingAgentTool(options);
    }
}
