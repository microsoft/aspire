// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Watches for resource snapshot changes from an AppHost backchannel connection
/// and maintains an up-to-date collection of resources.
/// </summary>
internal sealed class ResourceSnapshotWatcher : IDisposable
{
    internal const int UpdateBufferCapacity = 256;

    private readonly IAppHostAuxiliaryBackchannel _connection;
    private readonly Dictionary<string, ResourceSnapshot> _resources = new(StringComparers.ResourceName);
    private readonly ILogger<ResourceSnapshotWatcher> _logger;
    private readonly Channel<bool>? _updateSignal;
    private readonly Dictionary<string, ResourceSnapshotUpdate>? _pendingUpdates;
    private readonly object _resourcesLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _initialLoadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _watchTask;
    private int _updateConsumerClaimed;
    private long _updateSequence;
    private bool _resyncPending;
    public ResourceSnapshotWatcher(
        IAppHostAuxiliaryBackchannel connection,
        ILogger<ResourceSnapshotWatcher> logger,
        bool includeHidden = false,
        bool bufferUpdates = false)
    {
        _connection = connection;
        _logger = logger;
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
            if (!_connection.SupportsResourceSnapshotVersionsV1)
            {
                // Legacy peers always report version 0, so concurrent GET/watch results cannot be reconciled.
                // Preserve their original ordering by seeding the GET snapshot before starting the watch.
                var snapshots = await _connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
                lock (_resourcesLock)
                {
                    foreach (var snapshot in snapshots)
                    {
                        _resources[snapshot.Name] = snapshot;
                    }
                }

                _initialLoadTcs.TrySetResult();
                await WatchChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Start the watch before fetching the initial snapshot. The AppHost subscribes before replaying
                // its current snapshots, so the watch establishes a replay point even though the two JSON-RPC
                // calls are not ordered. Version reconciliation retains the newest observed snapshot.
                using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var watchTask = WatchChangesAsync(watchCts.Token);
                List<ResourceSnapshot> snapshots;
                try
                {
                    snapshots = await _connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup failures must not replace the initial GET failure.
                    try
                    {
                        watchCts.Cancel();
                    }
                    catch (Exception)
                    {
                    }

                    try
                    {
                        await watchTask.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }

                    throw;
                }

                lock (_resourcesLock)
                {
                    foreach (var snapshot in snapshots)
                    {
                        if (!_resources.TryGetValue(snapshot.Name, out var currentSnapshot) ||
                            snapshot.Version > currentSnapshot.Version)
                        {
                            _resources[snapshot.Name] = snapshot;
                        }
                    }
                }

                _initialLoadTcs.TrySetResult();
                await watchTask.ConfigureAwait(false);
            }

            _updateSignal?.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialLoadTcs.TrySetCanceled(cancellationToken);
            _updateSignal?.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _initialLoadTcs.TrySetException(ex);
            _updateSignal?.Writer.TryComplete(ex);
        }
    }

    private async Task WatchChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var snapshot in _connection.WatchResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false))
        {
            long? retainedVersion = null;
            lock (_resourcesLock)
            {
                if (_connection.SupportsResourceSnapshotVersionsV1 &&
                    _resources.TryGetValue(snapshot.Name, out var currentSnapshot) &&
                    snapshot.Version > 0 &&
                    currentSnapshot.Version > 0 &&
                    snapshot.Version < currentSnapshot.Version)
                {
                    // Version 0 is the compatibility value from older AppHosts and cannot establish
                    // ordering. Known lower versions can be stale replay events that arrive after
                    // initial GET/watch reconciliation, so they must not regress state or buffering.
                    retainedVersion = currentSnapshot.Version;
                }
                else
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
            }

            if (retainedVersion is not null)
            {
                _logger.LogDebug(
                    "Ignoring stale snapshot version {IncomingVersion} for resource '{ResourceName}'; retained version is {RetainedVersion}.",
                    snapshot.Version,
                    snapshot.Name,
                    retainedVersion);
                continue;
            }

            _updateSignal?.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Streams updates from the same subscription that maintains the current resource collection.
    /// Callers should first capture the initial state with <see cref="CaptureAllResources"/>.
    /// The update stream can be enumerated only once over the lifetime of the watcher.
    /// </summary>
    public async IAsyncEnumerable<ResourceSnapshotUpdateBatch> WatchResourceSnapshotBatchesAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialLoadComplete();
        var updateSignal = _updateSignal ?? throw new InvalidOperationException("Resource update buffering was not enabled for this watcher.");
        if (Interlocked.Exchange(ref _updateConsumerClaimed, 1) != 0)
        {
            throw new InvalidOperationException("Resource snapshot updates support only one consumer for the lifetime of this watcher.");
        }

        await foreach (var _ in updateSignal.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            ResourceSnapshotUpdate[] updates;
            bool isResync;
            lock (_resourcesLock)
            {
                if (_resyncPending)
                {
                    updates = _resources.Values
                        .Select(snapshot => new ResourceSnapshotUpdate(_updateSequence, snapshot))
                        .ToArray();
                    _resyncPending = false;
                    isResync = true;
                }
                else
                {
                    updates = _pendingUpdates!.Values
                        .ToArray();
                    _pendingUpdates.Clear();
                    isResync = false;
                }
            }

            var snapshots = isResync
                ? updates
                    .Where(update => update.Sequence > afterSequence)
                    .OrderBy(update => update.Snapshot.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(update => update.Snapshot)
                    .ToArray()
                : updates
                    .Where(update => update.Sequence > afterSequence)
                    .OrderBy(update => update.Sequence)
                    .Select(update => update.Snapshot)
                    .ToArray();

            if (snapshots.Length > 0)
            {
                yield return new ResourceSnapshotUpdateBatch(snapshots, isResync);
            }
        }
    }

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
        lock (_resourcesLock)
        {
            return _resources.GetValueOrDefault(name);
        }
    }

    /// <summary>
    /// Gets all current resource snapshots, using <see cref="IncludeHidden"/> to determine visibility.
    /// </summary>
    /// <returns>Resource snapshots, ordered by name.</returns>
    public IReadOnlyList<ResourceSnapshot> GetResources()
    {
        return GetResources(IncludeHidden);
    }

    /// <summary>
    /// Gets all current resource snapshots, including hidden resources.
    /// </summary>
    /// <returns>All resource snapshots, ordered by name.</returns>
    public IReadOnlyList<ResourceSnapshot> GetAllResources()
    {
        return GetResources(includeHidden: true);
    }

    /// <summary>
    /// Atomically captures the current resources and the last update represented by that state.
    /// </summary>
    public ResourceSnapshotCapture CaptureAllResources()
    {
        EnsureInitialLoadComplete();
        ResourceSnapshot[] resources;
        long updateSequence;
        lock (_resourcesLock)
        {
            resources = CopyResourcesNoLock();
            updateSequence = _updateSequence;
        }

        return new(FilterAndOrderResources(resources, includeHidden: true), updateSequence);
    }

    private IReadOnlyList<ResourceSnapshot> GetResources(bool includeHidden)
    {
        EnsureInitialLoadComplete();

        ResourceSnapshot[] resources;
        lock (_resourcesLock)
        {
            resources = CopyResourcesNoLock();
        }

        return FilterAndOrderResources(resources, includeHidden);
    }

    private ResourceSnapshot[] CopyResourcesNoLock() => [.. _resources.Values];

    private static IReadOnlyList<ResourceSnapshot> FilterAndOrderResources(
        IEnumerable<ResourceSnapshot> resources,
        bool includeHidden)
    {
        if (!includeHidden)
        {
            resources = resources.Where(snapshot => !ResourceSnapshotMapper.IsHiddenResource(snapshot));
        }

        return resources
            .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    internal readonly record struct ResourceSnapshotCapture(
        IReadOnlyList<ResourceSnapshot> Resources,
        long UpdateSequence);

    internal readonly record struct ResourceSnapshotUpdateBatch(
        IReadOnlyList<ResourceSnapshot> Snapshots,
        bool IsResync);

    private readonly record struct ResourceSnapshotUpdate(
        long Sequence,
        ResourceSnapshot Snapshot);
}
