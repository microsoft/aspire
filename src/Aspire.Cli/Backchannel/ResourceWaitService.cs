// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Identifies the resource condition requested by a wait operation.
/// </summary>
internal enum ResourceWaitTarget
{
    Healthy,
    Up,
    Down
}

/// <summary>
/// Identifies the outcome of a resource wait operation.
/// </summary>
internal enum ResourceWaitOutcome
{
    Success,

    /// <summary>
    /// The resource ran to completion with exit code 0 instead of reaching the requested
    /// running condition. Only produced by <see cref="ResourceWaitService.WaitUntilDoneAsync"/>.
    /// </summary>
    Completed,
    Timeout,
    Failure,

    /// <summary>
    /// The wait was abandoned because another resource in the same batch failed.
    /// Only produced by <see cref="ResourceWaitService.WaitUntilDoneAsync"/>.
    /// </summary>
    Cancelled
}

/// <summary>
/// Contains the interpreted result of one resource wait operation.
/// </summary>
internal sealed record ResourceWaitResult(
    ResourceWaitOutcome Outcome,
    string ResourceName,
    string? State,
    string? Health,
    bool ResourceNotFound,
    string? ErrorMessage,
    TimeSpan Elapsed)
{
    /// <summary>
    /// Gets the process exit code when the wait inspected an exited resource.
    /// </summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// Contains the results of a batch wait and the latest resource snapshots observed while waiting.
/// </summary>
/// <param name="Results">The result of each requested resource, in request order.</param>
/// <param name="Snapshots">
/// The latest snapshot of each resource the batch watched. The watch can end before the AppHost replayed every
/// resource, so callers that need a resource missing from this list must read the snapshots themselves.
/// </param>
internal sealed record ResourceWaitBatchResult(
    IReadOnlyList<ResourceWaitResult> Results,
    IReadOnlyList<ResourceSnapshot> Snapshots);

/// <summary>
/// Applies the shared Aspire backchannel wait semantics for a resource.
/// </summary>
internal sealed class ResourceWaitService(
    TimeProvider timeProvider,
    ILogger<ResourceWaitService> logger)
{
    // Matches Aspire.Hosting.Dcp.Model.ExecutableState.Terminated, which the CLI does not reference.
    private const string TerminatedState = "Terminated";

    // The AppHost publishes the snapshot that stopped a wait before it answers the wait request, but the
    // snapshot travels on a separate stream. This bounds how long a failure waits for that snapshot so a
    // failure with another cause (for example, a failed resource ready event) still fails the batch quickly.
    private static readonly TimeSpan s_completionGracePeriod = TimeSpan.FromSeconds(2);

    public async Task<ResourceWaitResult> WaitAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startTimestamp = timeProvider.GetTimestamp();
        return await WaitCoreAsync(
            connection,
            resourceName,
            target,
            timeoutSeconds,
            startTimestamp,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResourceWaitResult>> WaitForResourcesAsync(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<string> resourceNames,
        ResourceWaitTarget target,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        // Use one monotonic budget so UTC clock corrections cannot extend or expire the wait.
        var startTimestamp = timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var waitTasks = resourceNames
            .Select(resourceName => WaitWithinBudgetAsync(
                connection,
                resourceName,
                target,
                startTimestamp,
                timeout,
                cancellationToken))
            .ToArray();

        return await Task.WhenAll(waitTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a batch of resources until each one is done for the requested target.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="WaitForResourcesAsync"/>, this applies the semantics needed to wait for a
    /// whole application:
    /// <list type="bullet">
    /// <item>
    /// For <see cref="ResourceWaitTarget.Healthy"/> and <see cref="ResourceWaitTarget.Up"/>, a resource that
    /// exited with code 0 (for example, a database migration) is reported as <see cref="ResourceWaitOutcome.Completed"/>.
    /// The AppHost's healthy and up waits stop on terminal states and report them as failures, the same way
    /// <c>WaitFor</c> does, so the exit code is read from the resource snapshot after such a failure.
    /// </item>
    /// <item>
    /// The first failure cancels the remaining waits, which are reported as <see cref="ResourceWaitOutcome.Cancelled"/>,
    /// so a crashed resource does not leave the caller waiting for the full timeout.
    /// </item>
    /// <item>
    /// While the batch is in progress, resource snapshots are watched for exits. A resource without health checks
    /// is healthy as soon as it runs, so a one-shot resource can satisfy its own wait and exit later. A nonzero
    /// exit observed before the batch ends still fails the batch, and an exit with code 0 is reported as completed.
    /// </item>
    /// </list>
    /// </remarks>
    public async Task<ResourceWaitBatchResult> WaitUntilDoneAsync(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<string> resourceNames,
        ResourceWaitTarget target,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startTimestamp = timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var failFastCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Snapshot reads only refine a result the AppHost already returned, so they must neither
        // outlive the --timeout budget nor keep a failed batch waiting.
        using var deadlineCts = new CancellationTokenSource(timeout, timeProvider);
        using var resolutionCts = CancellationTokenSource.CreateLinkedTokenSource(failFastCts.Token, deadlineCts.Token);

        // A "down" wait is satisfied by any exit, so exits only need watching for running targets.
        // The snapshots are still watched for "down" so callers can show final states without another read.
        var monitor = new BatchSnapshotMonitor(connection, resourceNames, watchForExits: target is not ResourceWaitTarget.Down);
        var watchTask = WatchSnapshotsAsync(connection, monitor, failFastCts, watchCts.Token);

        BatchWaitOutcome[] outcomes;
        try
        {
            var waitTasks = resourceNames
                .Select(resourceName => WaitUntilDoneCoreAsync(
                    connection,
                    resourceName,
                    target,
                    startTimestamp,
                    timeout,
                    monitor,
                    failFastCts,
                    resolutionCts.Token,
                    cancellationToken))
                .ToArray();

            outcomes = await Task.WhenAll(waitTasks).ConfigureAwait(false);
        }
        finally
        {
            await watchCts.CancelAsync().ConfigureAwait(false);
            await watchTask.ConfigureAwait(false);
        }

        // Read the observations only after the watcher has completed.
        var snapshots = monitor.GetSnapshots();
        var results = outcomes
            .Select((outcome, index) =>
            {
                var result = outcome.Result;

                // A resolution interrupted by the deadline or by another resource's failure still deserves
                // whatever the watcher learned before the batch ended, without waiting any longer.
                if (outcome.ResolutionInterrupted &&
                    FindResourceSnapshots(snapshots, result.ResourceName) is [var snapshot] &&
                    IsExited(snapshot))
                {
                    result = ApplyExitedSnapshot(result, snapshot);
                }

                return ApplyObservedExit(result, monitor.GetObservedExit(index));
            })
            .ToArray();

        return new ResourceWaitBatchResult(results, snapshots);
    }

    private async Task WatchSnapshotsAsync(
        IAppHostAuxiliaryBackchannel connection,
        BatchSnapshotMonitor monitor,
        CancellationTokenSource failFastCts,
        CancellationToken cancellationToken)
    {
        try
        {
            // The AppHost replays every current snapshot before streaming changes, so the monitor
            // also serves as the batch's view of the application.
            await foreach (var snapshot in connection.WatchResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false))
            {
                if (monitor.Observe(snapshot))
                {
                    await failFastCts.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Watching is best effort: without it, the per-resource waits still report every resource,
            // they just cannot notice a resource that exits after its own wait already succeeded.
            logger.LogDebug(
                "Watching resource snapshots for exits failed with {ExceptionType}",
                ex.GetType().Name);
        }
        finally
        {
            monitor.CompleteWatch();
        }
    }

    private static ResourceWaitResult ApplyObservedExit(ResourceWaitResult result, ResourceSnapshot? exit)
    {
        // A failure without a state did not come from the resource stopping (for example, an ambiguous
        // replica name or an AppHost without wait support). Its error message is more useful than an exit.
        if (exit is null || result is { Outcome: ResourceWaitOutcome.Failure, State: null })
        {
            return result;
        }

        if (IsFailedExit(exit))
        {
            return result with
            {
                Outcome = ResourceWaitOutcome.Failure,
                State = exit.State,
                Health = exit.HealthStatus,
                ExitCode = exit.ExitCode
            };
        }

        return result.Outcome is ResourceWaitOutcome.Success or ResourceWaitOutcome.Cancelled or ResourceWaitOutcome.Timeout
            ? result with
            {
                Outcome = ResourceWaitOutcome.Completed,
                State = exit.State,
                Health = exit.HealthStatus,
                ErrorMessage = null,
                ExitCode = exit.ExitCode
            }
            : result;
    }

    private static bool IsExited(ResourceSnapshot snapshot)
    {
        // DCP reports "Terminated" when its controller killed the process (for example, on scale-down or
        // deletion). Snapshots forward that state unchanged and it may have no exit code, and the AppHost's
        // healthy wait does not stop on it, so it must be recognized here to fail the wait.
        return IsTerminalFailureState(snapshot.State) ||
            string.Equals(snapshot.State, TerminatedState, StringComparisons.ResourceState) ||
            (snapshot.ExitCode is not null &&
             (string.Equals(snapshot.State, KnownResourceStates.Finished, StringComparisons.ResourceState) ||
              string.Equals(snapshot.State, KnownResourceStates.Exited, StringComparisons.ResourceState)));
    }

    private static bool IsFailedExit(ResourceSnapshot snapshot)
    {
        return IsExited(snapshot) && !IsSuccessfulCompletion(snapshot);
    }

    /// <summary>
    /// Returns whether <paramref name="snapshot"/> shows a state at which the AppHost's healthy or up wait stops.
    /// </summary>
    private static bool IsStoppedWaitState(ResourceSnapshot snapshot)
    {
        // Mirrors the AppHost predicates: the healthy wait also stops on RuntimeUnhealthy, and the up wait
        // stops on any exit code or a state with the "error" style (KnownResourceStateStyles.Error).
        return IsExited(snapshot) ||
            snapshot.ExitCode is not null ||
            string.Equals(snapshot.State, KnownResourceStates.RuntimeUnhealthy, StringComparisons.ResourceState) ||
            string.Equals(snapshot.StateStyle, "error", StringComparisons.ResourceState);
    }

    private async Task<BatchWaitOutcome> WaitUntilDoneCoreAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        long startTimestamp,
        TimeSpan timeout,
        BatchSnapshotMonitor monitor,
        CancellationTokenSource failFastCts,
        CancellationToken resolutionToken,
        CancellationToken cancellationToken)
    {
        var resourceStartTimestamp = timeProvider.GetTimestamp();
        ResourceWaitResult result;
        try
        {
            result = await WaitWithinBudgetAsync(
                connection,
                resourceName,
                target,
                startTimestamp,
                timeout,
                failFastCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (failFastCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new BatchWaitOutcome(
                new ResourceWaitResult(
                    ResourceWaitOutcome.Cancelled,
                    resourceName,
                    State: null,
                    Health: null,
                    ResourceNotFound: false,
                    ErrorMessage: null,
                    timeProvider.GetElapsedTime(resourceStartTimestamp)),
                ResolutionInterrupted: false);
        }

        var resolutionInterrupted = false;

        // A "down" wait already succeeds for any exit code, so only running targets need to
        // distinguish a successful run-to-completion from a crash.
        if (result.Outcome == ResourceWaitOutcome.Failure &&
            !result.ResourceNotFound &&
            target is not ResourceWaitTarget.Down)
        {
            try
            {
                result = await ResolveCompletionAsync(result, monitor, resolutionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The deadline passed or another resource failed. The AppHost's failure is still known,
                // so keep it and let the batch reconcile it with the watcher's final snapshots.
                resolutionInterrupted = true;
            }
        }

        if (result.Outcome == ResourceWaitOutcome.Failure)
        {
            await failFastCts.CancelAsync().ConfigureAwait(false);
        }

        return new BatchWaitOutcome(result, resolutionInterrupted);
    }

    private async Task<ResourceWaitResult> ResolveCompletionAsync(
        ResourceWaitResult result,
        BatchSnapshotMonitor monitor,
        CancellationToken resolutionToken)
    {
        // Snapshots the AppHost observed before failing the wait can still be in flight on the watch.
        var failureSequence = monitor.MarkFailureObserved();
        using var graceCts = new CancellationTokenSource(s_completionGracePeriod, timeProvider);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(resolutionToken, graceCts.Token);
        while (true)
        {
            // Cancellation also ends the watch, which wakes this loop; stop rather than fall back to a read.
            resolutionToken.ThrowIfCancellationRequested();
            var view = monitor.Find(result.ResourceName);

            // The AppHost rejects a display name shared by several replicas before waiting, and other
            // failures (an AppHost without wait support, RPC errors) are not about the resource's state.
            // Only a single resource that actually exited can explain the failure; otherwise keep the
            // original error message.
            if (view.Matches.Count > 1)
            {
                return result;
            }

            if (view.Matches is [var snapshot] && IsStoppedWaitState(snapshot))
            {
                return IsExited(snapshot) ? ApplyExitedSnapshot(result, snapshot) : result;
            }

            if (view.WatchEnded)
            {
                break;
            }

            try
            {
                await view.Changed.WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (graceCts.IsCancellationRequested && !resolutionToken.IsCancellationRequested)
            {
                // The live watch never showed the resource stopping, so the failure has another cause.
                return result;
            }
        }

        // Without a live watch, fall back to reading the snapshots. The read is shared by every resource
        // resolving at the same time, so a batch of completed jobs does not read the whole application per job.
        List<ResourceSnapshot> snapshots;
        try
        {
            snapshots = await monitor.ReadSnapshotsAsync(failureSequence, resolutionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                "Unable to read the exit code of resource '{ResourceName}' after a failed wait: {ExceptionType}",
                result.ResourceName,
                ex.GetType().Name);
            return result;
        }

        return FindResourceSnapshots(snapshots, result.ResourceName) is [var readSnapshot] && IsExited(readSnapshot)
            ? ApplyExitedSnapshot(result, readSnapshot)
            : result;
    }

    private static ResourceWaitResult ApplyExitedSnapshot(ResourceWaitResult result, ResourceSnapshot snapshot)
    {
        if (IsSuccessfulCompletion(snapshot))
        {
            return result with
            {
                Outcome = ResourceWaitOutcome.Completed,
                State = snapshot.State,
                Health = snapshot.HealthStatus,
                ErrorMessage = null,
                ExitCode = 0
            };
        }

        // Surface what the snapshot shows so callers can explain the failure, for example
        // "Exited (exit code: 1)". The healthy wait does not return a state when it stops early.
        return result with
        {
            Outcome = ResourceWaitOutcome.Failure,
            State = snapshot.State,
            Health = snapshot.HealthStatus,
            ExitCode = snapshot.ExitCode
        };
    }

    /// <summary>
    /// Finds the snapshots that match <paramref name="resourceName"/>: an exact runtime name first, otherwise
    /// every replica with that display name. The AppHost only waits when this returns a single snapshot.
    /// </summary>
    internal static IReadOnlyList<ResourceSnapshot> FindResourceSnapshots(IEnumerable<ResourceSnapshot> snapshots, string resourceName)
    {
        var snapshotList = snapshots as IReadOnlyList<ResourceSnapshot> ?? snapshots.ToList();
        var runtimeMatch = snapshotList.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Name, resourceName, StringComparisons.ResourceName));
        if (runtimeMatch is not null)
        {
            return [runtimeMatch];
        }

        return snapshotList
            .Where(snapshot => string.Equals(snapshot.DisplayName, resourceName, StringComparisons.ResourceName))
            .ToArray();
    }

    private static bool IsSuccessfulCompletion(ResourceSnapshot snapshot)
    {
        return snapshot.ExitCode == 0 &&
            (string.Equals(snapshot.State, KnownResourceStates.Finished, StringComparisons.ResourceState) ||
             string.Equals(snapshot.State, KnownResourceStates.Exited, StringComparisons.ResourceState));
    }

    private async Task<ResourceWaitResult> WaitWithinBudgetAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        long startTimestamp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var resourceStartTimestamp = timeProvider.GetTimestamp();
        var remaining = timeout - timeProvider.GetElapsedTime(startTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return new ResourceWaitResult(
                ResourceWaitOutcome.Timeout,
                resourceName,
                State: null,
                Health: null,
                ResourceNotFound: false,
                ErrorMessage: null,
                timeProvider.GetElapsedTime(resourceStartTimestamp));
        }

        // The backchannel accepts whole seconds. Round up so a fractional second remaining on
        // the shared deadline is not truncated into an early timeout.
        var remainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        try
        {
            return await WaitCoreAsync(
                connection,
                resourceName,
                target,
                remainingSeconds,
                resourceStartTimestamp,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Wait for resource '{ResourceName}' failed with {ExceptionType}",
                resourceName,
                ex.GetType().Name);
            return new ResourceWaitResult(
                ResourceWaitOutcome.Failure,
                resourceName,
                State: null,
                Health: null,
                ResourceNotFound: false,
                ErrorMessage: null,
                timeProvider.GetElapsedTime(resourceStartTimestamp));
        }
    }

    private async Task<ResourceWaitResult> WaitCoreAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        int timeoutSeconds,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        var response = await connection.WaitForResourceAsync(
            resourceName,
            GetProtocolValue(target),
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        // The AppHost's "down" predicate treats FailedToStart as terminal. It still represents
        // a failed resource and must not become a successful wait result.
        var outcome = response switch
        {
            _ when IsTerminalFailureState(response.State) => ResourceWaitOutcome.Failure,
            { Success: true } => ResourceWaitOutcome.Success,
            { ResourceNotFound: true } => ResourceWaitOutcome.Failure,
            { TimedOut: true } => ResourceWaitOutcome.Timeout,
            _ => ResourceWaitOutcome.Failure
        };

        return new ResourceWaitResult(
            outcome,
            resourceName,
            response.State,
            response.HealthStatus,
            response.ResourceNotFound,
            response.ErrorMessage,
            timeProvider.GetElapsedTime(startTimestamp));
    }

    internal static bool IsTerminalFailureState(string? state)
    {
        return string.Equals(
            state,
            KnownResourceStates.FailedToStart,
            StringComparisons.ResourceState);
    }

    internal static string GetProtocolValue(ResourceWaitTarget target)
    {
        return target switch
        {
            ResourceWaitTarget.Healthy => "healthy",
            ResourceWaitTarget.Up => "up",
            ResourceWaitTarget.Down => "down",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }

    private readonly record struct BatchWaitOutcome(ResourceWaitResult Result, bool ResolutionInterrupted);

    private readonly record struct SnapshotView(IReadOnlyList<ResourceSnapshot> Matches, Task Changed, bool WatchEnded);

    /// <summary>
    /// Holds what a batch wait learned from the resource snapshot watch: the latest snapshot of every resource,
    /// and the exits of the waited resources.
    /// </summary>
    private sealed class BatchSnapshotMonitor(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<string> resourceNames,
        bool watchForExits)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, ResourceSnapshot> _latest = new(StringComparers.ResourceName);
        private readonly ResourceSnapshot?[] _observedExits = new ResourceSnapshot?[resourceNames.Count];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _watchEnded;
        private long _failureSequence;
        private Task<List<ResourceSnapshot>>? _read;
        private long _readSequence;

        /// <summary>
        /// Records a snapshot from the watch and returns whether it is a failed exit of a waited resource.
        /// </summary>
        public bool Observe(ResourceSnapshot snapshot)
        {
            var failedExit = false;
            TaskCompletionSource changed;
            lock (_lock)
            {
                StoreLatest(snapshot);

                if (watchForExits && IsExited(snapshot))
                {
                    for (var i = 0; i < resourceNames.Count; i++)
                    {
                        // Wait requests accept a runtime name or a display name shared by all replicas.
                        if (!string.Equals(snapshot.Name, resourceNames[i], StringComparisons.ResourceName) &&
                            !string.Equals(snapshot.DisplayName, resourceNames[i], StringComparisons.ResourceName))
                        {
                            continue;
                        }

                        // Keep a failed replica's exit once seen so a sibling replica that exits with 0 cannot hide it.
                        if (_observedExits[i] is not { } previous || !IsFailedExit(previous))
                        {
                            _observedExits[i] = snapshot;
                        }

                        failedExit |= IsFailedExit(snapshot);
                    }
                }

                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
            return failedExit;
        }

        public void CompleteWatch()
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                _watchEnded = true;
                changed = _changed;
            }

            changed.TrySetResult();
        }

        /// <summary>
        /// Gets the latest snapshots that match <paramref name="resourceName"/>, with a task that completes on the
        /// next change so callers can wait for the watch to catch up without missing an update.
        /// </summary>
        public SnapshotView Find(string resourceName)
        {
            lock (_lock)
            {
                return new SnapshotView(FindResourceSnapshots(_latest.Values.ToArray(), resourceName), _changed.Task, _watchEnded);
            }
        }

        public IReadOnlyList<ResourceSnapshot> GetSnapshots()
        {
            lock (_lock)
            {
                return _latest.Values.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        public ResourceSnapshot? GetObservedExit(int index)
        {
            lock (_lock)
            {
                return _observedExits[index];
            }
        }

        public long MarkFailureObserved()
        {
            return Interlocked.Increment(ref _failureSequence);
        }

        /// <summary>
        /// Reads every resource snapshot, sharing a read with the other resources that need one.
        /// </summary>
        public Task<List<ResourceSnapshot>> ReadSnapshotsAsync(long failureSequence, CancellationToken cancellationToken)
        {
            Task<List<ResourceSnapshot>> read;
            lock (_lock)
            {
                // A read that started before this failure was observed can predate the snapshot that explains it.
                if (_read is null || _readSequence < failureSequence || _read.IsFaulted || _read.IsCanceled)
                {
                    _readSequence = Interlocked.Read(ref _failureSequence);
                    _read = ReadAndStoreAsync(cancellationToken);
                }

                read = _read;
            }

            return read.WaitAsync(cancellationToken);
        }

        private async Task<List<ResourceSnapshot>> ReadAndStoreAsync(CancellationToken cancellationToken)
        {
            var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);

            // Reads only happen once the watch ended, so keep them for the batch's final view of the application.
            lock (_lock)
            {
                foreach (var snapshot in snapshots)
                {
                    StoreLatest(snapshot);
                }
            }

            return snapshots;
        }

        private void StoreLatest(ResourceSnapshot snapshot)
        {
            // Version 0 comes from AppHosts that do not version snapshots and cannot establish ordering.
            // Otherwise a lower version is a stale replay that must not replace a newer snapshot.
            if (_latest.TryGetValue(snapshot.Name, out var current) &&
                snapshot.Version > 0 &&
                current.Version > 0 &&
                snapshot.Version < current.Version)
            {
                return;
            }

            _latest[snapshot.Name] = snapshot;
        }
    }
}
