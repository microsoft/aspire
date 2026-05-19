// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that grounds an agent's responses using Bing Search.
/// </summary>
/// <remarks>
/// <para>
/// The Bing Search resource (<c>Microsoft.Bing/accounts</c>) must be created manually in
/// the <a href="https://portal.azure.com">Azure portal</a> before using this tool.
/// </para>
/// <para>
/// After creating the tool with <see cref="PromptAgentBuilderExtensions.AddBingGroundingTool"/>,
/// link it using one of the <c>WithReference</c> overloads on <see cref="PromptAgentBuilderExtensions"/>
/// with one of the following:
/// <list type="bullet">
/// <item>A <see cref="BingGroundingConnectionResource"/> created by
/// <see cref="AzureCognitiveServicesProjectConnectionsBuilderExtensions.AddBingGroundingConnection(IResourceBuilder{AzureCognitiveServicesProjectResource}, string, string)"/>.</item>
/// <item>A Bing resource ID string to auto-create a connection.</item>
/// <item>A <see cref="IResourceBuilder{T}"/> for <see cref="ParameterResource"/>
/// containing the Bing resource ID.</item>
/// </list>
/// </para>
/// </remarks>
[AspireExport]
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
    /// Gets or sets the Bing grounding connection resource for the Bing Search service.
    /// </summary>
    internal BingGroundingConnectionResource? Connection { get; set; }

    /// <inheritdoc/>
    public override async Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        if (Connection is null)
        {
            throw new InvalidOperationException(
                $"Bing Grounding tool '{Name}' does not have a project connection. " +
                "Ensure the tool was added using AddBingGroundingTool().");
        }

        var connectionIdRef = new BicepOutputReference("id", Connection);
        var connectionId = await connectionIdRef.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connectionId))
        {
            throw new InvalidOperationException(
                $"Failed to resolve connection ID for Bing Grounding tool '{Name}'. " +
                "The Foundry project connection may not have been provisioned correctly.");
        }

        var config = new BingGroundingSearchConfiguration(connectionId);
        var options = new BingGroundingSearchToolOptions([config]);
        return new BingGroundingTool(options);
    }
}
