// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Watches for resource snapshot changes from an AppHost backchannel connection
/// and maintains an up-to-date collection of resources.
/// </summary>
internal sealed class ResourceSnapshotWatcher : IDisposable
{
    internal const int UpdateBufferCapacity = 256;

    private readonly IAppHostAuxiliaryBackchannel _connection;
    private readonly ConcurrentDictionary<string, ResourceSnapshot> _resources = new(StringComparers.ResourceName);
    private readonly Channel<bool>? _updateSignal;
    private readonly Dictionary<string, ResourceSnapshotUpdate>? _pendingUpdates;
    private readonly object _resourcesLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _initialLoadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _watchTask;
    private long _updateSequence;
    private bool _resyncPending;
    private volatile Exception? _watchException;

    public ResourceSnapshotWatcher(
        IAppHostAuxiliaryBackchannel connection,
        bool includeHidden = false,
        bool bufferUpdates = false)
    {
        _connection = connection;
        IncludeHidden = includeHidden;
        if (bufferUpdates)
        {
            _updateSignal = Channel.CreateBounded<bool>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropWrite
                });
            _pendingUpdates = new Dictionary<string, ResourceSnapshotUpdate>(StringComparers.ResourceName);
        }
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
            // Start the watch before fetching the initial snapshot so a resource transition cannot
            // fall into the gap between those two backchannel calls. Changes win over the initial
            // snapshot because the snapshot may already be stale by the time it is returned.
            using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watchTask = WatchChangesAsync(watchCts.Token);
            List<ResourceSnapshot> snapshots;
            try
            {
                snapshots = await _connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Exception? cancellationException = null;
                try
                {
                    watchCts.Cancel();
                }
                catch (Exception ex)
                {
                    // Preserve the initial-load exception while retaining a cancellation callback
                    // failure for diagnostics after the already-started watch has been observed.
                    cancellationException = ex;
                }

                try
                {
                    await watchTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException watchException) when (
                    watchException.CancellationToken == watchCts.Token ||
                    watchException.CancellationToken == default && watchCts.IsCancellationRequested)
                {
                    // This cancellation is the expected result of stopping the watch after the
                    // initial snapshot failed, not an independent watch-loop failure.
                }
                catch (Exception watchException)
                {
                    // Preserve the initial-load exception for callers while still observing any
                    // independent failure raised as the already-started watch is canceled.
                    _watchException = watchException;
                }

                _watchException ??= cancellationException;
                throw;
            }

            lock (_resourcesLock)
            {
                foreach (var snapshot in snapshots)
                {
                    _resources.TryAdd(snapshot.Name, snapshot);
                }
            }

            _initialLoadTcs.TrySetResult();
            await watchTask.ConfigureAwait(false);
            _updateSignal?.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialLoadTcs.TrySetCanceled(cancellationToken);
            _updateSignal?.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            if (!_initialLoadTcs.TrySetException(ex))
            {
                // Initial load already completed; store for callers to detect.
                _watchException = ex;
            }
            _updateSignal?.Writer.TryComplete(ex);
        }
    }

    private async Task WatchChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var snapshot in _connection.WatchResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false))
        {
            lock (_resourcesLock)
            {
                _resources[snapshot.Name] = snapshot;
                var update = new ResourceSnapshotUpdate(++_updateSequence, snapshot);
                if (_pendingUpdates is not null && !_resyncPending)
                {
                    if (_pendingUpdates.ContainsKey(snapshot.Name) || _pendingUpdates.Count < UpdateBufferCapacity)
                    {
                        _pendingUpdates[snapshot.Name] = update;
                    }
                    else
                    {
                        // Once the bounded per-resource buffer is full, the current dictionary is the
                        // coalesced representation. The consumer will resynchronize from it rather than
                        // retaining every intermediate transition or stalling the AppHost event stream.
                        _pendingUpdates.Clear();
                        _resyncPending = true;
                    }
                }
            }
            _updateSignal?.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Streams updates from the same subscription that maintains the current resource collection.
    /// Callers should first capture the initial state with <see cref="CaptureAllResources"/>.
    /// </summary>
    public async IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialLoadComplete();
        var updateSignal = _updateSignal ?? throw new InvalidOperationException("Resource update buffering was not enabled for this watcher.");
        await foreach (var _ in updateSignal.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            ResourceSnapshotUpdate[] updates;
            lock (_resourcesLock)
            {
                if (_resyncPending)
                {
                    updates = _resources.Values
                        .Select(snapshot => new ResourceSnapshotUpdate(_updateSequence, snapshot))
                        .OrderBy(update => update.Snapshot.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    _resyncPending = false;
                }
                else
                {
                    updates = _pendingUpdates!.Values
                        .OrderBy(update => update.Sequence)
                        .ToArray();
                    _pendingUpdates.Clear();
                }
            }

            foreach (var update in updates)
            {
                if (update.Sequence > afterSequence)
                {
                    yield return update.Snapshot;
                }
            }
        }
    }

    /// <summary>
    /// Gets an independent exception that terminated the watch loop, or <see langword="null"/> if no watch failure was observed.
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

    /// <summary>
    /// Atomically captures the current resources and the last update represented by that state.
    /// </summary>
    public ResourceSnapshotCapture CaptureAllResources()
    {
        EnsureInitialLoadComplete();
        lock (_resourcesLock)
        {
            return new(GetResources(includeHidden: true).ToList(), _updateSequence);
        }
    }

    private IEnumerable<ResourceSnapshot> GetResources(bool includeHidden)
    {
        EnsureInitialLoadComplete();

        lock (_resourcesLock)
        {
            var snapshots = _resources.Values.AsEnumerable();

            if (!includeHidden)
            {
                snapshots = snapshots.Where(s => !ResourceSnapshotMapper.IsHiddenResource(s));
            }

            return snapshots.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    internal readonly record struct ResourceSnapshotCapture(
        IReadOnlyList<ResourceSnapshot> Resources,
        long UpdateSequence);

    private readonly record struct ResourceSnapshotUpdate(
        long Sequence,
        ResourceSnapshot Snapshot);
}
