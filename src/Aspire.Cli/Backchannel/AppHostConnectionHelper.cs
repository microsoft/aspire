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
    /// Gets the appropriate AppHost connection based on the selection logic:
    /// 1. If a specific AppHost is selected via select_apphost, use that
    /// 2. Otherwise, look for in-scope connections (AppHosts within the working directory)
    /// 3. If exactly one in-scope connection exists, use it
    /// 4. If multiple in-scope connections exist, throw an error listing them
    /// 5. If no in-scope connections exist, fall back to the first available connection
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

        if (connections.Count == 0)
        {
            await auxiliaryBackchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
            connections = auxiliaryBackchannelMonitor.Connections.ToList();
            if (connections.Count == 0)
            {
                return null;
            }
        }

        // Check if a specific AppHost was selected
        var selectedPath = auxiliaryBackchannelMonitor.SelectedAppHostPath;
        if (!string.IsNullOrEmpty(selectedPath))
        {
            var selectedConnection = connections.FirstOrDefault(c =>
                c.AppHostInfo?.AppHostPath != null &&
                string.Equals(c.AppHostInfo.AppHostPath, selectedPath, StringComparison.OrdinalIgnoreCase));

            if (selectedConnection != null)
            {
                logger.LogDebug("Using explicitly selected AppHost: {AppHostPath}", selectedPath);
                return selectedConnection;
            }

            logger.LogWarning("Selected AppHost at '{SelectedPath}' is no longer running, falling back to selection logic", selectedPath);
            // Clear the selection since the AppHost is no longer available
            auxiliaryBackchannelMonitor.SelectedAppHostPath = null;
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
            var paths = inScopeConnections
                .Where(c => c.AppHostInfo?.AppHostPath != null)
                .Select(c => c.AppHostInfo!.AppHostPath)
                .ToList();

            var pathsList = string.Join("\n", paths.Select(p => $"  - {p}"));

            throw new McpProtocolException(
                $"Multiple Aspire AppHosts are running in the scope of the MCP server's working directory. " +
                $"Use the 'select_apphost' tool to specify which AppHost to use.\n\nRunning AppHosts:\n{pathsList}",
                McpErrorCode.InternalError);
        }

        var fallback = connections
            .OrderBy(c => c.AppHostInfo?.AppHostPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.AppHostInfo?.ProcessId ?? int.MaxValue)
            .FirstOrDefault();

        logger.LogDebug(
            "No in-scope AppHosts found. Falling back to first available AppHost: {AppHostPath}",
            fallback?.AppHostInfo?.AppHostPath ?? "N/A");

        return fallback;
    }
}
