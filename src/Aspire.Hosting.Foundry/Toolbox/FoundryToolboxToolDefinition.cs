// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Base type for Microsoft Foundry Toolbox tool definitions.
/// </summary>
public abstract class FoundryToolboxToolDefinition
{
    private protected FoundryToolboxToolDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
    }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Resolves this tool definition into the SDK shape (<see cref="ProjectsAgentTool"/>) used by the
    /// Foundry data plane when creating a new toolbox version.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal abstract ValueTask<ProjectsAgentTool> ToProjectsAgentToolAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Describes a web search tool in a Microsoft Foundry Toolbox.
/// </summary>
public sealed class FoundryToolboxWebSearchToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxWebSearchToolDefinition(string name)
        : base(name)
    {
    }

    internal override ValueTask<ProjectsAgentTool> ToProjectsAgentToolAsync(CancellationToken cancellationToken)
    {
        // ResponseTool.CreateWebSearchTool() produces an OpenAI WebSearchTool; AsAgentTool() lifts
        // it into the Azure.AI.Projects.Agents wire shape that AgentToolboxes.CreateToolboxVersion
        // expects.
        return new ValueTask<ProjectsAgentTool>(ResponseTool.CreateWebSearchTool().AsAgentTool());
    }
}

/// <summary>
/// Describes an MCP tool in a Microsoft Foundry Toolbox.
/// </summary>
public sealed class FoundryToolboxMcpToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxMcpToolDefinition(string name, ReferenceExpression endpointExpression)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(endpointExpression);

        EndpointExpression = endpointExpression;
    }

    /// <summary>
    /// Gets the MCP endpoint expression for the tool.
    /// </summary>
    public ReferenceExpression EndpointExpression { get; }

    internal override async ValueTask<ProjectsAgentTool> ToProjectsAgentToolAsync(CancellationToken cancellationToken)
    {
        var endpoint = await EndpointExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException(
                $"MCP tool '{Name}' does not have a resolvable endpoint URI.");
        }

        // NOTE: When the MCP endpoint points at a compute resource owned by this app (for example an
        // EndpointReference from a project, container, or executable), the published URL is only
        // reachable after that resource has been deployed. Toolbox creation does not currently take
        // a pipeline dependency on referenced compute, so an MCP tool that points at a sibling
        // resource may need a manual `WaitFor(...)` to ensure correct ordering during `aspire deploy`.
        return ResponseTool.CreateMcpTool(serverLabel: Name, serverUri: new Uri(endpoint)).AsAgentTool();
    }
}

/// <summary>
/// Describes an Azure AI Search tool in a Microsoft Foundry Toolbox.
/// </summary>
public sealed class FoundryToolboxAzureAISearchToolDefinition : FoundryToolboxToolDefinition
{
    internal FoundryToolboxAzureAISearchToolDefinition(
        string name,
        AzureSearchResource searchResource,
        AzureCognitiveServicesProjectConnectionResource connection,
        string? indexName)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(searchResource);
        ArgumentNullException.ThrowIfNull(connection);

        SearchResource = searchResource;
        Connection = connection;
        IndexName = indexName;
    }

    /// <summary>
    /// Gets the Azure AI Search resource backing this tool.
    /// </summary>
    public AzureSearchResource SearchResource { get; }

    /// <summary>
    /// Gets the Foundry project connection resource used by the tool.
    /// </summary>
    public AzureCognitiveServicesProjectConnectionResource Connection { get; }

    /// <summary>
    /// Gets the optional Azure AI Search index name.
    /// </summary>
    public string? IndexName { get; }

    internal override async ValueTask<ProjectsAgentTool> ToProjectsAgentToolAsync(CancellationToken cancellationToken)
    {
        // The Foundry project connection's "id" bicep output is only populated after provisioning,
        // so this resolves to a real value only at deploy time. Matches AzureAISearchToolResource.
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
