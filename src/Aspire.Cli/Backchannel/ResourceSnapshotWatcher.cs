// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Watches for resource snapshot changes from an AppHost backchannel connection
/// and maintains an up-to-date collection of resources.
/// </summary>
internal sealed class ResourceSnapshotWatcher : IDisposable
{
    private readonly IAppHostAuxiliaryBackchannel _connection;
    private readonly ConcurrentDictionary<string, ResourceSnapshot> _resources = new(StringComparers.ResourceName);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _initialLoadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _watchTask;
    private volatile Exception? _watchException;

    public ResourceSnapshotWatcher(IAppHostAuxiliaryBackchannel connection, bool includeHidden = false)
    {
        _connection = connection;
        IncludeHidden = includeHidden;
        _watchTask = WatchAsync(_cts.Token);
    }

    /// <summary>
    /// Gets a value indicating whether hidden resources are included by default in <see cref="GetResources()"/>.
    /// </summary>
    public bool IncludeHidden { get; }

    /// <summary>
    /// Waits until the initial resource snapshot load is complete.
    /// </summary>
    public Task WaitForInitialLoadAsync(CancellationToken cancellationToken = default)
    {
        return _initialLoadTcs.Task.WaitAsync(cancellationToken);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshots = await _connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);

            foreach (var snapshot in snapshots)
            {
                _resources[snapshot.Name] = snapshot;
            }

            _initialLoadTcs.TrySetResult();

            await foreach (var snapshot in _connection.WatchResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false))
            {
                _resources[snapshot.Name] = snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialLoadTcs.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            if (!_initialLoadTcs.TrySetException(ex))
            {
                // Initial load already completed; store for callers to detect.
                _watchException = ex;
            }
        }
    }

    /// <summary>
    /// Gets the exception that terminated the watch loop after the initial load, or <see langword="null"/> if the watch is still running.
    /// </summary>
    public Exception? WatchException => _watchException;

    private void EnsureInitialLoadComplete()
    {
        if (!_initialLoadTcs.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Initial resource snapshot load has not completed. Call WaitForInitialLoadAsync first.");
        }
    }

    /// <summary>
    /// Gets a resource snapshot by name, or <see langword="null"/> if not found.
    /// </summary>
    public ResourceSnapshot? GetResource(string name)
    {
        EnsureInitialLoadComplete();
        return _resources.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets all current resource snapshots, using <see cref="IncludeHidden"/> to determine visibility.
    /// </summary>
    /// <returns>Resource snapshots, ordered by name.</returns>
    public IEnumerable<ResourceSnapshot> GetResources()
    {
        return GetResources(IncludeHidden);
    }

    /// <summary>
    /// Gets all current resource snapshots, including hidden resources.
    /// </summary>
    /// <returns>All resource snapshots, ordered by name.</returns>
    public IEnumerable<ResourceSnapshot> GetAllResources()
    {
        return GetResources(includeHidden: true);
    }

    private IEnumerable<ResourceSnapshot> GetResources(bool includeHidden)
    {
        EnsureInitialLoadComplete();

        var snapshots = _resources.Values.AsEnumerable();

        if (!includeHidden)
        {
            snapshots = snapshots.Where(s => !ResourceSnapshotMapper.IsHiddenResource(s));
        }

        return snapshots.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
