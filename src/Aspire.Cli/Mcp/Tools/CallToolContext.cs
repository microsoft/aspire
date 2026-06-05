// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// Provides context for executing MCP tools.
/// </summary>
internal sealed class CallToolContext
{
    /// <summary>
    /// Gets the MCP notifier for sending notifications.
    /// </summary>
    public required IMcpNotifier Notifier { get; init; }

    /// <summary>
    /// Gets the MCP client instance to use for communicating with the dashboard.
    /// </summary>
    public required ModelContextProtocol.Client.McpClient? McpClient { get; init; }

    /// <summary>
    /// Gets the arguments passed to the tool.
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement>? Arguments { get; init; }

    /// <summary>
    /// Gets the progress token for reporting progress updates, if provided by the client.
    /// </summary>
    public required ProgressToken? ProgressToken { get; init; }
}
