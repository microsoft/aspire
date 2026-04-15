// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Watches for resource snapshot changes from an AppHost backchannel connection
/// and maintains an up-to-date collection of resources.
/// </summary>
internal sealed class ResourceWatcher : IDisposable
{
    private readonly IAppHostAuxiliaryBackchannel _connection;
    private readonly ConcurrentDictionary<string, ResourceSnapshot> _resources = new(StringComparers.ResourceName);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watchTask;

    public ResourceWatcher(IAppHostAuxiliaryBackchannel connection)
    {
        _connection = connection;
        _watchTask = WatchAsync(_cts.Token);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in _connection.WatchResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false))
            {
                _resources[snapshot.Name] = snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when disposed.
        }
    }

    /// <summary>
    /// Gets a resource snapshot by name, or <see langword="null"/> if not found.
    /// </summary>
    public ResourceSnapshot? GetResource(string name)
    {
        return _resources.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets all current resource snapshots.
    /// </summary>
    /// <param name="includeHidden">When <see langword="true"/>, includes hidden resources in the result.</param>
    /// <returns>A list of resource snapshots, ordered by name.</returns>
    public IReadOnlyList<ResourceSnapshot> GetResources(bool includeHidden)
    {
        var snapshots = _resources.Values.AsEnumerable();

        if (!includeHidden)
        {
            snapshots = snapshots.Where(s => !ResourceSnapshotMapper.IsHiddenResource(s));
        }

        return snapshots.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
