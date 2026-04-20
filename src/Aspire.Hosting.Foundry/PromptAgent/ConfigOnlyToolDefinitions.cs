// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Azure.AI.Projects.OpenAI;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool definition that grounds an agent's responses using SharePoint data.
/// </summary>
/// <remarks>
/// SharePoint connections must be configured in the Foundry project beforehand.
/// This tool references existing connections by their Foundry project connection IDs.
/// </remarks>
public sealed class SharePointToolDefinition : IFoundryTool
{
    /// <summary>
    /// Creates a new instance of the <see cref="SharePointToolDefinition"/> class.
    /// </summary>
    /// <param name="projectConnectionIds">The Foundry project connection IDs for the SharePoint sites.</param>
    public SharePointToolDefinition(params string[] projectConnectionIds)
    {
        ArgumentNullException.ThrowIfNull(projectConnectionIds);
        ProjectConnectionIds = projectConnectionIds.ToList();
    }

    /// <summary>
    /// Gets the Foundry project connection IDs for the SharePoint sites.
    /// </summary>
    public IList<string> ProjectConnectionIds { get; }

    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        var options = new SharePointGroundingToolOptions();
        foreach (var connectionId in ProjectConnectionIds)
        {
            options.ProjectConnections.Add(new ToolProjectConnection(connectionId));
        }

        return Task.FromResult<ResponseTool>(new SharepointAgentTool(options));
    }
}

/// <summary>
/// A Foundry tool definition that enables an agent to query data using a Microsoft Fabric data agent.
/// </summary>
/// <remarks>
/// Fabric connections must be configured in the Foundry project beforehand.
/// This tool references existing connections by their Foundry project connection IDs.
/// </remarks>
public sealed class FabricToolDefinition : IFoundryTool
{
    /// <summary>
    /// Creates a new instance of the <see cref="FabricToolDefinition"/> class.
    /// </summary>
    /// <param name="projectConnectionIds">The Foundry project connection IDs for the Fabric data agents.</param>
    public FabricToolDefinition(params string[] projectConnectionIds)
    {
        ArgumentNullException.ThrowIfNull(projectConnectionIds);
        ProjectConnectionIds = projectConnectionIds.ToList();
    }

    /// <summary>
    /// Gets the Foundry project connection IDs for the Fabric data agents.
    /// </summary>
    public IList<string> ProjectConnectionIds { get; }

    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        var options = new FabricDataAgentToolOptions();
        foreach (var connectionId in ProjectConnectionIds)
        {
            options.ProjectConnections.Add(new ToolProjectConnection(connectionId));
        }

        return Task.FromResult<ResponseTool>(new MicrosoftFabricAgentTool(options));
    }
}
