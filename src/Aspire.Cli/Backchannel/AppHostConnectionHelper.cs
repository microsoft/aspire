// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Provides helper methods for working with AppHost connections.
/// </summary>
internal static class AppHostConnectionHelper
{
    /// <summary>
    /// Finds the connection whose AppHost project has the same filesystem identity as <paramref name="appHostPath"/>.
    /// </summary>
    public static IAppHostAuxiliaryBackchannel? FindConnectionByAppHostPath(
        IEnumerable<IAppHostAuxiliaryBackchannel> connections,
        string appHostPath)
    {
        return connections.SingleOrDefault(connection =>
            connection.AppHostInfo?.AppHostPath is { } candidatePath &&
            AppHostPathComparer.PathsEqual(candidatePath, appHostPath));
    }

    /// <summary>
    /// Gets the appropriate AppHost connection for MCP operations.
    /// </summary>
    /// <param name="auxiliaryBackchannelMonitor">The backchannel monitor to get connections from.</param>
    /// <param name="logger">Logger for debug output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected connection, or null if none available.</returns>
    public static async Task<IAppHostAuxiliaryBackchannel?> GetSelectedConnectionAsync(
        IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var connections = auxiliaryBackchannelMonitor.Connections.ToList();
        var scannedConnections = false;

        if (connections.Count == 0)
        {
            await auxiliaryBackchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
            scannedConnections = true;
            connections = auxiliaryBackchannelMonitor.Connections.ToList();
        }

        // Check if a specific AppHost was selected
        var selectedPath = auxiliaryBackchannelMonitor.SelectedAppHostPath;
        if (!string.IsNullOrEmpty(selectedPath))
        {
            var selectedConnection = FindSelectedConnection(connections, selectedPath, logger);

            if (selectedConnection is null && !scannedConnections)
            {
                // Explicit selections need one fresh scan before we report the pinned AppHost
                // unavailable. Cached unrelated connections can otherwise hide a newly started
                // AppHost until the next background scan.
                await auxiliaryBackchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                connections = auxiliaryBackchannelMonitor.Connections.ToList();
                selectedConnection = FindSelectedConnection(connections, selectedPath, logger);
            }

            if (selectedConnection is not null)
            {
                logger.LogDebug("Using explicitly selected AppHost: {AppHostPath}", selectedPath);
                return selectedConnection;
            }

            logger.LogWarning("The explicitly selected AppHost is unavailable: {AppHostPath}", selectedPath);
            throw new McpProtocolException(
                "The selected AppHost is not available. Start that AppHost and retry.",
                McpErrorCode.InternalError);
        }

        if (connections.Count == 0)
        {
            return null;
        }

        // Get in-scope connections
        var inScopeConnections = connections.Where(c => c.IsInScope).ToList();

        if (inScopeConnections.Count == 1)
        {
            logger.LogDebug("Using single in-scope AppHost: {AppHostPath}", inScopeConnections[0].AppHostInfo?.AppHostPath ?? "N/A");
            return inScopeConnections[0];
        }

        if (inScopeConnections.Count > 1)
        {
            throw new McpProtocolException(
                "Multiple Aspire AppHosts are running in the MCP server's working directory scope. " +
                "Use 'select_apphost' to choose the AppHost for this request.",
                McpErrorCode.InternalError);
        }

        throw new McpProtocolException(
            "Running Aspire AppHosts were found outside the MCP server's working directory scope. " +
            "Use 'list_apphosts' to discover available AppHosts, then 'select_apphost' to choose one.",
            McpErrorCode.InternalError);
    }

    private static IAppHostAuxiliaryBackchannel? FindSelectedConnection(
        IEnumerable<IAppHostAuxiliaryBackchannel> connections,
        string selectedPath,
        ILogger logger)
    {
        try
        {
            return FindConnectionByAppHostPath(connections, selectedPath);
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Multiple running AppHost instances match the selected path: {AppHostPath}", selectedPath);
            throw new McpProtocolException(
                "Multiple running AppHost instances match the selected path. Stop the extra instance and retry.",
                McpErrorCode.InternalError);
        }
    }
}
