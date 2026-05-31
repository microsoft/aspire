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

    /// <summary>
    /// Gets or sets an optional expression that resolves to the bearer token sent to the MCP
    /// server on every request. Use this for MCP servers that require authorization.
    /// </summary>
    /// <remarks>
    /// When set, the resolved value is forwarded to the Foundry data plane and the Foundry agent
    /// runtime sends it as the <c>Authorization</c> header (with the standard <c>Bearer</c> scheme)
    /// when invoking the MCP server. The expression is resolved at toolbox deploy time so it can
    /// safely reference <see cref="ApplicationModel.ParameterResource"/> instances or other
    /// deploy-time values.
    /// </remarks>
    public ReferenceExpression? AuthorizationTokenExpression { get; set; }

    /// <summary>
    /// Gets the set of HTTP headers the Foundry agent runtime sends to the MCP server on every
    /// request. Header names are matched case-insensitively, mirroring HTTP semantics.
    /// </summary>
    /// <remarks>
    /// Each value is a <see cref="ReferenceExpression"/> that is resolved at toolbox deploy time
    /// so headers can safely reference <see cref="ApplicationModel.ParameterResource"/> instances
    /// or other deploy-time values. Header entries whose value resolves to <see langword="null"/>
    /// or an empty string are silently omitted.
    /// </remarks>
    public IDictionary<string, ReferenceExpression> Headers { get; } =
        new Dictionary<string, ReferenceExpression>(StringComparer.OrdinalIgnoreCase);

    internal override async ValueTask<ProjectsAgentTool> ToProjectsAgentToolAsync(CancellationToken cancellationToken)
    {
        var endpoint = await EndpointExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException(
                $"MCP tool '{Name}' does not have a resolvable endpoint URI.");
        }

        string? authorizationToken = null;
        if (AuthorizationTokenExpression is not null)
        {
            authorizationToken = await AuthorizationTokenExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(authorizationToken))
            {
                throw new InvalidOperationException(
                    $"MCP tool '{Name}' was configured with an authorization token but the token expression resolved to an empty value.");
            }
        }

        // Resolve every configured header to its concrete value, dropping any that produce a
        // null/empty string (Headers is intended for explicit values; empty values are a no-op
        // rather than a configuration error).
        IDictionary<string, string>? headers = null;
        if (Headers.Count > 0)
        {
            headers = new Dictionary<string, string>(Headers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var header in Headers)
            {
                if (string.IsNullOrEmpty(header.Key))
                {
                    throw new InvalidOperationException(
                        $"MCP tool '{Name}' has a header with an empty name.");
                }

                var value = await header.Value.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(value))
                {
                    headers[header.Key] = value;
                }
            }

            // Drop the dictionary if every entry resolved to an empty value: the SDK distinguishes
            // null from an empty collection in some wire forms, and we prefer "no headers configured"
            // semantics when nothing was actually supplied.
            if (headers.Count == 0)
            {
                headers = null;
            }
        }

        // Always go through the full ResponseTool.CreateMcpTool overload so the auth/header path is
        // exercised consistently. Both parameters accept null with the same effect as omitting them.
        return ResponseTool.CreateMcpTool(
            serverLabel: Name,
            serverUri: new Uri(endpoint),
            authorizationToken: authorizationToken,
            serverDescription: null,
            headers: headers,
            allowedTools: null,
            toolCallApprovalPolicy: null).AsAgentTool();
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
