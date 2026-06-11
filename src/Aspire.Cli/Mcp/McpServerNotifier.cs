// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ModelContextProtocol.Server;

namespace Aspire.Cli.Mcp;

/// <summary>
/// Implementation of <see cref="IMcpNotifier"/> that wraps an <see cref="McpServer"/>.
/// </summary>
internal sealed class McpServerNotifier(McpServer server) : IMcpNotifier
{
    /// <inheritdoc />
    public Task SendNotificationAsync(string method, CancellationToken cancellationToken = default)
    {
        return server.SendNotificationAsync(method, cancellationToken);
    }

    /// <inheritdoc />
    public Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default)
    {
        return server.SendNotificationAsync(method, parameters, cancellationToken: cancellationToken);
    }
}
