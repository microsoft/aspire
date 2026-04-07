// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAuxiliaryBackchannelMonitor : IAuxiliaryBackchannelMonitor
{
    // Outer key: hash, Inner key: socketPath
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IAppHostAuxiliaryBackchannel>> _connectionsByHash = new();

    public IEnumerable<IAppHostAuxiliaryBackchannel> Connections =>
        _connectionsByHash.Values.SelectMany(d => d.Values);

    public IEnumerable<IAppHostAuxiliaryBackchannel> GetConnectionsByHash(string hash) =>
        _connectionsByHash.TryGetValue(hash, out var connections) ? connections.Values : [];

    public string? SelectedAppHostPath { get; set; }

    /// <summary>
    /// Gets the number of times ScanAsync was called.
    /// </summary>
    public int ScanCallCount { get; private set; }

    /// <summary>
    /// Triggers an immediate scan. In the test implementation, this just increments ScanCallCount.
    /// </summary>
    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        ScanCallCount++;
        return Task.CompletedTask;
    }

    public IAppHostAuxiliaryBackchannel? SelectedConnection
    {
        get
        {
            var connections = Connections.ToList();

            if (connections.Count == 0)
            {
                return null;
            }

            // Check if a specific AppHost was selected
            if (!string.IsNullOrEmpty(SelectedAppHostPath))
            {
                var selectedConnection = connections.FirstOrDefault(c =>
                    c.AppHostInfo?.AppHostPath != null &&
                    string.Equals(Path.GetFullPath(c.AppHostInfo.AppHostPath), Path.GetFullPath(SelectedAppHostPath), StringComparison.OrdinalIgnoreCase));

                if (selectedConnection != null)
                {
                    return selectedConnection;
                }

                // Clear the selection since the AppHost is no longer available
                SelectedAppHostPath = null;
            }

            // Look for in-scope connections
            var inScopeConnections = connections.Where(c => c.IsInScope).ToList();

            if (inScopeConnections.Count == 1)
            {
                return inScopeConnections[0];
            }

            // Fall back to the first available connection
            return connections.FirstOrDefault();
        }
    }

    public IReadOnlyList<IAppHostAuxiliaryBackchannel> GetConnectionsForWorkingDirectory(DirectoryInfo workingDirectory)
    {
        return Connections
            .Where(c => IsAppHostInScopeOfDirectory(c.AppHostInfo?.AppHostPath, workingDirectory.FullName))
            .ToList();
    }

    private static bool IsAppHostInScopeOfDirectory(string? appHostPath, string workingDirectory)
    {
        if (string.IsNullOrEmpty(appHostPath))
        {
            return false;
        }

        // Normalize the paths for comparison
        var normalizedWorkingDirectory = Path.GetFullPath(workingDirectory);
        var normalizedAppHostPath = Path.GetFullPath(appHostPath);

        // Check if the AppHost path is within the working directory
        var relativePath = Path.GetRelativePath(normalizedWorkingDirectory, normalizedAppHostPath);
        return !relativePath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativePath);
    }

    public void AddConnection(string hash, string socketPath, IAppHostAuxiliaryBackchannel connection)
    {
        var connectionsDict = _connectionsByHash.GetOrAdd(hash, _ => new ConcurrentDictionary<string, IAppHostAuxiliaryBackchannel>());
        connectionsDict[socketPath] = connection;
    }

    public void RemoveConnection(string hash, string socketPath)
    {
        if (_connectionsByHash.TryGetValue(hash, out var connectionsDict))
        {
            connectionsDict.TryRemove(socketPath, out _);
            if (connectionsDict.IsEmpty)
            {
                _connectionsByHash.TryRemove(hash, out _);
            }
        }
    }

    public void ClearConnections()
    {
        _connectionsByHash.Clear();
    }
}
