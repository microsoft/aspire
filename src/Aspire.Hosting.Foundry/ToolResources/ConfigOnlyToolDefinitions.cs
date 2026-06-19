// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that grounds an agent's responses using SharePoint data.
/// </summary>
/// <remarks>
/// SharePoint connections must be configured in the Foundry project beforehand.
/// This tool references existing connections by their Foundry project connection IDs.
/// </remarks>
[AspireExport]
public sealed class SharePointToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="SharePointToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="projectConnectionIds">The Foundry project connection IDs for the SharePoint sites.</param>
    public SharePointToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project,
        params string[] projectConnectionIds)
        : base(name, project)
    {
        ArgumentNullException.ThrowIfNull(projectConnectionIds);
        ProjectConnectionIds = projectConnectionIds.ToList();
    }

    /// <summary>
    /// Gets the Foundry project connection IDs for the SharePoint sites.
    /// </summary>
    public IList<string> ProjectConnectionIds { get; }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        var options = new SharePointGroundingToolOptions();
        foreach (var connectionId in ProjectConnectionIds)
        {
            options.ProjectConnections.Add(new ToolProjectConnection(connectionId));
        }

        return Task.FromResult<ResponseTool>(new SharepointPreviewTool(options));
    }
}

/// <summary>
/// A Foundry tool resource that enables an agent to query data using a Microsoft Fabric data agent.
/// </summary>
/// <remarks>
/// Fabric connections must be configured in the Foundry project beforehand.
/// This tool references existing connections by their Foundry project connection IDs.
/// </remarks>
[AspireExport]
public sealed class FabricToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="FabricToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="projectConnectionIds">The Foundry project connection IDs for the Fabric data agents.</param>
    public FabricToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project,
        params string[] projectConnectionIds)
        : base(name, project)
    {
        ArgumentNullException.ThrowIfNull(projectConnectionIds);
        ProjectConnectionIds = projectConnectionIds.ToList();
    }

    /// <summary>
    /// Gets the Foundry project connection IDs for the Fabric data agents.
    /// </summary>
    public IList<string> ProjectConnectionIds { get; }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        var options = new FabricDataAgentToolOptions();
        foreach (var connectionId in ProjectConnectionIds)
        {
            options.ProjectConnections.Add(new ToolProjectConnection(connectionId));
        }

        return Task.FromResult<ResponseTool>(new MicrosoftFabricPreviewTool(options));
    }
}
