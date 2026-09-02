// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAuxiliaryBackchannelMonitor : IAuxiliaryBackchannelMonitor
{
    private readonly ConcurrentDictionary<string, IAppHostAuxiliaryBackchannel> _connectionsBySocketPath = new(StringComparers.FileSystemPath);
    private readonly Channel<bool> _connectionChanges = Channel.CreateUnbounded<bool>();

    public IEnumerable<IAppHostAuxiliaryBackchannel> Connections =>
        _connectionsBySocketPath.Values;

    public string? SelectedAppHostPath { get; set; }

    /// <summary>
    /// Gets the number of times ScanAsync was called.
    /// </summary>
    public int ScanCallCount { get; private set; }

    public Func<CancellationToken, Task>? ScanAsyncCallback { get; set; }

    /// <summary>
    /// Triggers an immediate scan. In the test implementation, this just increments ScanCallCount.
    /// </summary>
    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        ScanCallCount++;
        return ScanAsyncCallback?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> WatchConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ScanAsync(cancellationToken).ConfigureAwait(false);
        yield return Connections.ToList();

        await foreach (var _ in _connectionChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await ScanAsync(cancellationToken).ConfigureAwait(false);
            yield return Connections.ToList();
        }
    }

    public void NotifyConnectionsChanged()
    {
        _connectionChanges.Writer.TryWrite(true);
    }

    public IAppHostAuxiliaryBackchannel? SelectedConnection
    {
        get
        {
            // Delegates to the production policy so this fake cannot drift from the behavior
            // selection tests are meant to be asserting on.
            var selectedAppHostPath = SelectedAppHostPath;
            var connection = AuxiliaryBackchannelMonitor.SelectConnection(Connections, ref selectedAppHostPath);
            SelectedAppHostPath = selectedAppHostPath;
            return connection;
        }
    }

    public IReadOnlyList<IAppHostAuxiliaryBackchannel> GetConnectionsForWorkingDirectory(DirectoryInfo workingDirectory)
    {
        return Connections
            .Where(c => AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(c.AppHostInfo?.AppHostPath, workingDirectory.FullName))
            .ToList();
    }

    public void AddConnection(string socketPath, IAppHostAuxiliaryBackchannel connection)
    {
        _connectionsBySocketPath[socketPath] = connection;
    }

    public void RemoveConnection(string socketPath)
    {
        _connectionsBySocketPath.TryRemove(socketPath, out _);
    }

    public void ClearConnections()
    {
        _connectionsBySocketPath.Clear();
    }
}
