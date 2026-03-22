// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Cli.Mcp;

/// <summary>
/// Service responsible for refreshing resource-based MCP tools and sending tool list change notifications.
/// </summary>
internal interface IMcpResourceToolRefreshService
{
    /// <summary>
    /// Attempts to get the current resource tool map if it is valid (not invalidated and AppHost hasn't changed).
    /// </summary>
    /// <param name="resourceToolMap">When this method returns <c>true</c>, contains the current resource tool map.</param>
    /// <returns><c>true</c> if the tool map is valid and no refresh is needed; otherwise, <c>false</c>.</returns>
    bool TryGetResourceToolMap(out IReadOnlyDictionary<string, ResourceToolEntry> resourceToolMap);

    /// <summary>
    /// Marks the resource tool map as needing a refresh.
    /// </summary>
    void InvalidateToolMap();

    /// <summary>
    /// Refreshes the resource tool map by discovering MCP tools from connected resources.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the refreshed resource tool map and a flag indicating whether the tool set changed.</returns>
    Task<(IReadOnlyDictionary<string, ResourceToolEntry> ToolMap, bool Changed)> RefreshResourceToolMapAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a tools list changed notification to connected MCP clients.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendToolsListChangedNotificationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets the MCP server instance used for sending notifications.
    /// </summary>
    /// <param name="server">The MCP server, or null to clear.</param>
    void SetMcpServer(McpServer? server);
}

/// <summary>
/// Represents an entry in the resource tool map.
/// </summary>
/// <param name="ResourceName">The name of the resource that exposes the tool.</param>
/// <param name="Tool">The MCP tool definition.</param>
internal sealed record ResourceToolEntry(string ResourceName, Tool Tool);
