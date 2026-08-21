// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Cli.Mcp;

/// <summary>
/// Service responsible for refreshing resource-based MCP tools and sending tool list change notifications.
/// </summary>
internal sealed class McpResourceToolRefreshService : IMcpResourceToolRefreshService
{
    private readonly IAuxiliaryBackchannelMonitor _auxiliaryBackchannelMonitor;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private McpServer? _server;
    private ResourceToolMapSnapshot _snapshot = new(
        null,
        new Dictionary<string, ResourceToolEntry>(StringComparer.Ordinal));
    private bool _invalidated = true;

    public McpResourceToolRefreshService(
        IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
        ILogger<McpResourceToolRefreshService> logger)
    {
        _auxiliaryBackchannelMonitor = auxiliaryBackchannelMonitor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool TryGetResourceToolMap(
        IAppHostAuxiliaryBackchannel? connection,
        out ResourceToolMapSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_invalidated || !IsSameConnection(connection))
            {
                snapshot = null!;
                return false;
            }

            snapshot = _snapshot;
            return true;
        }
    }

    private bool IsSameConnection(IAppHostAuxiliaryBackchannel? connection)
        => ReferenceEquals(_snapshot.Connection, connection);

    /// <inheritdoc/>
    public void InvalidateToolMap()
    {
        lock (_lock)
        {
            _invalidated = true;
        }
    }

    /// <inheritdoc/>
    public void SetMcpServer(McpServer? server)
    {
        _server = server;
    }

    /// <inheritdoc/>
    public async Task SendToolsListChangedNotificationAsync(CancellationToken cancellationToken)
    {
        if (_server is { } server)
        {
            await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<(ResourceToolMapSnapshot Snapshot, bool Changed)> RefreshResourceToolMapAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Refreshing resource tool map.");

        var refreshedMap = new Dictionary<string, ResourceToolEntry>(StringComparer.Ordinal);

        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(_auxiliaryBackchannelMonitor, _logger, cancellationToken).ConfigureAwait(false);

        if (connection is not null)
        {
            try
            {
                var allResources = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
                var resourcesWithTools = allResources.Where(r => r.McpServer is not null && !McpToolHelpers.IsExcludedFromMcp(r)).ToList();

                _logger.LogDebug("Resources with MCP tools received: {Count}", resourcesWithTools.Count);

                foreach (var resource in resourcesWithTools)
                {
                    Debug.Assert(resource.McpServer is not null);

                    // Use DisplayName (the app-model name, e.g. "db1-mcp") rather than Name
                    // (the DCP runtime ID, e.g. "db1-mcp-ypnvhwvw") because the AppHost resolves
                    // resources by their app-model name in CallResourceMcpToolAsync.
                    var routedResourceName = resource.DisplayName ?? resource.Name;

                    foreach (var tool in resource.McpServer.Tools)
                    {
                        var exposedName = $"{routedResourceName.Replace("-", "_")}_{tool.Name}";
                        refreshedMap[exposedName] = new ResourceToolEntry(routedResourceName, tool);

                        _logger.LogDebug("{Tool}: {Description}", exposedName, tool.Description);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Resource discovery failures should not hide the CLI's built-in MCP tools.
                _logger.LogDebug(ex, "Failed to refresh resource MCP tool routing map.");
            }
        }
        else
        {
            _logger.LogDebug("Unable to refresh resource tool map because there's no selected connection.");
        }

        lock (_lock)
        {
            var changed = _snapshot.ToolMap.Count != refreshedMap.Count;
            if (!changed)
            {
                // Check for deleted tools (in old but not in new).
                foreach (var key in _snapshot.ToolMap.Keys)
                {
                    if (!refreshedMap.ContainsKey(key))
                    {
                        changed = true;
                        break;
                    }
                }

                // Check for new tools (in new but not in old).
                if (!changed)
                {
                    foreach (var key in refreshedMap.Keys)
                    {
                        if (!_snapshot.ToolMap.ContainsKey(key))
                        {
                            changed = true;
                            break;
                        }
                    }
                }
            }

            _snapshot = new ResourceToolMapSnapshot(connection, refreshedMap);
            _invalidated = false;
            return (_snapshot, changed);
        }
    }

}
