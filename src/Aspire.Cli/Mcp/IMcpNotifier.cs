// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Mcp;

/// <summary>
/// Interface for sending MCP notifications.
/// </summary>
internal interface IMcpNotifier
{
    /// <summary>
    /// Sends a notification to the MCP client.
    /// </summary>
    /// <param name="method">The notification method name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendNotificationAsync(string method, CancellationToken cancellationToken = default);

    Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default);
}
