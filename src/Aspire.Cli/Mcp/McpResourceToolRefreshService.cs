// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
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
    private Dictionary<string, ResourceToolEntry> _resourceToolMap = new(StringComparer.Ordinal);
    private bool _invalidated = true;
    private string? _lastRefreshedAppHostPath;

    public McpResourceToolRefreshService(
        IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
        ILogger<McpResourceToolRefreshService> logger)
    {
        _auxiliaryBackchannelMonitor = auxiliaryBackchannelMonitor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool TryGetResourceToolMap(out IReadOnlyDictionary<string, ResourceToolEntry> resourceToolMap)
    {
        lock (_lock)
        {
            if (_invalidated || _lastRefreshedAppHostPath != _auxiliaryBackchannelMonitor.ResolvedAppHostPath)
            {
                resourceToolMap = null!;
                return false;
            }

            resourceToolMap = _resourceToolMap;
            return true;
        }
    }

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
    public async Task<(IReadOnlyDictionary<string, ResourceToolEntry> ToolMap, bool Changed)> RefreshResourceToolMapAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Refreshing resource tool map.");

        var refreshedMap = new Dictionary<string, ResourceToolEntry>(StringComparer.Ordinal);

        string? selectedAppHostPath = null;
        try
        {
            var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(_auxiliaryBackchannelMonitor, _logger, cancellationToken).ConfigureAwait(false);

            if (connection is not null)
            {
                selectedAppHostPath = connection.AppHostInfo?.AppHostPath;

                var allResources = await connection.GetResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                var resourcesWithTools = allResources.Where(r => r.McpServer is not null).ToList();

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
            else
            {
                _logger.LogDebug("Unable to refresh resource tool map because there's no selected connection.");
            }
        }
        catch (Exception ex)
        {
            // Don't fail refresh_tools if resource discovery fails; still emit notification.
            _logger.LogDebug(ex, "Failed to refresh resource MCP tool routing map.");
        }

        lock (_lock)
        {
            var changed = _resourceToolMap.Count != refreshedMap.Count;
            if (!changed)
            {
                // Check for deleted tools (in old but not in new).
                foreach (var key in _resourceToolMap.Keys)
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
                        if (!_resourceToolMap.ContainsKey(key))
                        {
                            changed = true;
                            break;
                        }
                    }
                }
            }

            _resourceToolMap = refreshedMap;
            _lastRefreshedAppHostPath = selectedAppHostPath;
            _invalidated = false;
            return (_resourceToolMap, changed);
        }
    }

}
