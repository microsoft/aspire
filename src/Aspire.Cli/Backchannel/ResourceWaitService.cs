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

    /// <summary>
    /// Gets whether the AppHost rejected the requested name because several replicas share it.
    /// </summary>
    public bool ResourceAmbiguous { get; init; }
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

    // Older AppHosts do not return the exit code with a failed wait. They publish the snapshot that stopped
    // the wait before answering, but it travels on the separate watch stream, which the AppHost can hold up
    // while it builds another resource's snapshot (for example, discovering its MCP tools). This bounds how
    // long a failure waits for the watch before reading the snapshots directly, which a batch of completed
    // jobs shares, rather than giving up on the exit code.
    private static readonly TimeSpan s_completionGracePeriod = TimeSpan.FromSeconds(2);

    // The AppHost enforces a wait's timeout itself and answers with a timed-out response that carries the
    // resource's state. The CLI also bounds the call so a stalled AppHost or a lost reply cannot keep it waiting
    // forever, but allows this much longer so that the AppHost's own answer normally arrives first. The same
    // allowance bounds the snapshot read that reconciles a batch once its waits returned.
    private static readonly TimeSpan s_appHostReplyGracePeriod = TimeSpan.FromSeconds(2);

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
    /// <c>WaitFor</c> does. The AppHost returns the exit code with such a failure; for older AppHosts that do not,
    /// it is read from the resource snapshots instead.
    /// </item>
    /// <item>
    /// The first failure cancels the remaining waits, which are reported as <see cref="ResourceWaitOutcome.Cancelled"/>,
    /// so a crashed resource does not leave the caller waiting for the full timeout.
    /// </item>
    /// <item>
    /// While the batch is in progress, resource snapshots are watched for exits. A resource without health checks
    /// is healthy as soon as it runs, so a one-shot resource can satisfy its own wait and exit later. A nonzero
    /// exit observed before the batch ends still fails the batch, and an exit with code 0 is reported as completed.
    /// The watch can fall behind, so once every wait returned the snapshots are read once more for the same exits.
    /// </item>
    /// <item>
    /// A resource can stop before its exit code is reported. Such a stop neither completes nor fails the resource:
    /// the batch keeps waiting for the exit code, within the timeout, and reports the resource as timed out if the
    /// code is still unknown, or as cancelled if another resource failed the batch first.
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
        bool[] unresolvedExits;
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

            if (target is not ResourceWaitTarget.Down)
            {
                await ReconcileWithCurrentSnapshotsAsync(
                    connection,
                    outcomes,
                    monitor,
                    failFastCts,
                    startTimestamp,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }

            unresolvedExits = target is ResourceWaitTarget.Down
                ? new bool[outcomes.Length]
                : await WaitForPendingExitCodesAsync(outcomes, monitor, resolutionCts.Token, cancellationToken).ConfigureAwait(false);
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
                    FindExplainingSnapshot(result, FindResourceSnapshots(snapshots, result.ResourceName), monitor.HasReadSnapshots) is { } snapshot &&
                    IsExited(snapshot))
                {
                    result = ApplyExit(result, snapshot.State, snapshot.HealthStatus, snapshot.ExitCode);
                }

                result = ApplyObservedExit(result, monitor.GetObservedExit(index));

                // A resource that satisfied its wait and then stopped without an exit code before the batch
                // stopped waiting for it cannot be reported as ready or as completed.
                if (unresolvedExits[index] &&
                    result.Outcome == ResourceWaitOutcome.Success &&
                    FindResourceSnapshots(snapshots, result.ResourceName) is [var latest] &&
                    IsPendingExit(latest))
                {
                    result = ApplyPendingExit(result, latest.State, latest.HealthStatus, failFastCts.IsCancellationRequested);
                }

                return result;
            })
            .ToArray();

        return new ResourceWaitBatchResult(results, snapshots);
    }

    /// <summary>
    /// Reads the current snapshots once every wait returned, and records the exits the watch has not delivered yet.
    /// </summary>
    /// <remarks>
    /// A resource can satisfy its wait and then stop while the watch is held up (for example, while the AppHost
    /// builds another resource's snapshot). The last wait can return before the watch delivers that stop, so the
    /// watch alone cannot show that a successful batch is still running. The read is skipped when the batch already
    /// failed, since that outcome cannot change, and it is bounded by the shared deadline.
    /// </remarks>
    private async Task ReconcileWithCurrentSnapshotsAsync(
        IAppHostAuxiliaryBackchannel connection,
        BatchWaitOutcome[] outcomes,
        BatchSnapshotMonitor monitor,
        CancellationTokenSource failFastCts,
        long startTimestamp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (failFastCts.IsCancellationRequested ||
            !outcomes.Any(static outcome => outcome.Result.Outcome == ResourceWaitOutcome.Success))
        {
            return;
        }

        // The waits themselves may use the AppHost reply allowance, so the read gets the same one.
        var remaining = timeout + s_appHostReplyGracePeriod - timeProvider.GetElapsedTime(startTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        using var readDeadlineCts = new CancellationTokenSource(remaining, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readDeadlineCts.Token);
        try
        {
            var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, linkedCts.Token).ConfigureAwait(false);
            if (monitor.ObserveCurrentSnapshots(snapshots))
            {
                await failFastCts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Without the read, the batch still reports what the waits and the watch showed.
            logger.LogDebug(
                "Reading resource snapshots after the waits returned failed with {ExceptionType}",
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// Keeps the batch open while a resource that already satisfied its wait has stopped without an exit code yet,
    /// and returns which resources were still in that state when the batch stopped waiting.
    /// </summary>
    /// <remarks>
    /// DCP publishes the <c>Finished</c> or <c>Exited</c> state before the exit code, which arrives in a later
    /// snapshot. Ending the batch in between would report a resource that ran to completion, or crashed, as ready.
    /// Batches without such a resource end immediately.
    /// </remarks>
    private static async Task<bool[]> WaitForPendingExitCodesAsync(
        BatchWaitOutcome[] outcomes,
        BatchSnapshotMonitor monitor,
        CancellationToken resolutionToken,
        CancellationToken cancellationToken)
    {
        var pending = FindPendingExitCodes(out var changed, out var watchEnded);

        // Once the watch ended the exit codes can no longer arrive.
        while (!watchEnded && pending.Contains(true))
        {
            try
            {
                await changed.WaitAsync(resolutionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The deadline passed or a resource failed. Report the exits that are still pending.
                return FindPendingExitCodes(out _, out _);
            }

            pending = FindPendingExitCodes(out changed, out watchEnded);
        }

        return pending;

        bool[] FindPendingExitCodes(out Task changed, out bool watchEnded)
        {
            changed = Task.CompletedTask;
            watchEnded = false;
            var pending = new bool[outcomes.Length];
            for (var i = 0; i < outcomes.Length; i++)
            {
                var view = monitor.Find(outcomes[i].Result.ResourceName);

                // Take the change notification from the first lookup: a change between lookups completes it,
                // which only causes another pass.
                if (i == 0)
                {
                    changed = view.Changed;
                    watchEnded = view.WatchEnded;
                }

                pending[i] = outcomes[i].Result.Outcome == ResourceWaitOutcome.Success &&
                    view.Matches is [var snapshot] &&
                    IsPendingExit(snapshot);
            }

            return pending;
        }
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
        // A failure without a state did not come from the resource stopping (for example, a name the AppHost could
        // not resolve, or an AppHost without wait support). Its error message is more useful than an exit.
        if (exit is null ||
            result.ResourceNotFound ||
            result.ResourceAmbiguous ||
            result is { Outcome: ResourceWaitOutcome.Failure, State: null })
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
        return IsExited(snapshot.State, snapshot.ExitCode);
    }

    private static bool IsExited(string? state, int? exitCode)
    {
        // DCP reports "Terminated" when its controller killed the process (for example, on scale-down or
        // deletion). Snapshots forward that state unchanged and it may have no exit code, and the AppHost's
        // healthy wait does not stop on it, so it must be recognized here to fail the wait.
        return IsTerminalFailureState(state) ||
            string.Equals(state, TerminatedState, StringComparisons.ResourceState) ||
            (exitCode is not null &&
             (string.Equals(state, KnownResourceStates.Finished, StringComparisons.ResourceState) ||
              string.Equals(state, KnownResourceStates.Exited, StringComparisons.ResourceState)));
    }

    private static bool IsFailedExit(ResourceSnapshot snapshot)
    {
        return IsExited(snapshot) && !IsSuccessfulCompletion(snapshot);
    }

    private static bool IsPendingExit(ResourceSnapshot snapshot)
    {
        return IsPendingExit(snapshot.State, snapshot.ExitCode);
    }

    /// <summary>
    /// Returns whether the resource stopped but its exit code has not been reported yet.
    /// </summary>
    /// <remarks>
    /// DCP publishes the <c>Finished</c> or <c>Exited</c> state before the exit code, which arrives in a later
    /// snapshot. Until then the stop is neither a successful completion nor a failure.
    /// </remarks>
    private static bool IsPendingExit(string? state, int? exitCode)
    {
        return exitCode is null &&
            (string.Equals(state, KnownResourceStates.Finished, StringComparisons.ResourceState) ||
             string.Equals(state, KnownResourceStates.Exited, StringComparisons.ResourceState));
    }

    /// <summary>
    /// Reports a resource whose exit code was still unknown when the batch stopped waiting for it: the deadline
    /// passed (or the watch that delivers the code ended), or another resource failed the batch.
    /// </summary>
    private static ResourceWaitResult ApplyPendingExit(ResourceWaitResult result, string? state, string? health, bool batchFailed)
    {
        return result with
        {
            Outcome = batchFailed ? ResourceWaitOutcome.Cancelled : ResourceWaitOutcome.Timeout,
            State = state,
            Health = health,
            ErrorMessage = null,
            ExitCode = null
        };
    }

    /// <summary>
    /// Returns whether <paramref name="snapshot"/> shows a state at which the AppHost's healthy or up wait stops.
    /// </summary>
    private static bool IsStoppedWaitState(ResourceSnapshot snapshot)
    {
        // Mirrors the AppHost predicates: both waits stop on terminal states, the healthy wait also stops on
        // RuntimeUnhealthy, and the up wait stops on any exit code or a state with the "error" style
        // (KnownResourceStateStyles.Error).
        return IsExited(snapshot) ||
            IsPendingExit(snapshot) ||
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
        // distinguish a successful run-to-completion from a crash. A name the AppHost could not resolve
        // to a single resource is not about any resource's state.
        if (result.Outcome == ResourceWaitOutcome.Failure &&
            !result.ResourceNotFound &&
            !result.ResourceAmbiguous &&
            target is not ResourceWaitTarget.Down)
        {
            // Current AppHosts return the state and exit code that stopped the wait, which settles the outcome
            // without depending on the snapshot stream.
            if (IsExited(result.State, result.ExitCode))
            {
                result = ApplyExit(result, result.State, result.Health, result.ExitCode);
            }
            else
            {
                try
                {
                    result = await ResolveCompletionAsync(result, monitor, resolutionToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The deadline passed or another resource failed. The AppHost's failure is still known, so keep
                    // it unless the resource is known to have stopped, and let the batch reconcile it with the
                    // watcher's final snapshots.
                    resolutionInterrupted = true;
                    result = ApplyInterruptedResolution(result, monitor, failFastCts.IsCancellationRequested);
                }
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
                // The watch may not have replayed every replica that shares the name yet, so read the snapshots.
                if (FindExplainingSnapshot(result, view.Matches, view.HasReadSnapshots) is null)
                {
                    break;
                }

                if (IsPendingExit(snapshot))
                {
                    return await WaitForExitCodeAsync(result, monitor, resolutionToken).ConfigureAwait(false);
                }

                return IsExited(snapshot) ? ApplyExit(result, snapshot.State, snapshot.HealthStatus, snapshot.ExitCode) : result;
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
                // The watch has not shown the resource stopping yet. It may be delayed, so read the snapshots.
                break;
            }
        }

        // Read the snapshots when the watch ended or fell behind. Reads are shared by every resource resolving
        // at the same time, so a batch of completed jobs does not read the whole application per job. They are
        // bounded by the batch deadline rather than the grace period, so a slow AppHost cannot turn a completed
        // job into a failure, and they stop as soon as another resource fails the batch.
        IReadOnlyList<ResourceSnapshot> snapshots;
        try
        {
            var read = await monitor.ReadSnapshotsAsync(failureSequence, acceptEarlierRead: true, resolutionToken).ConfigureAwait(false);
            if (!ExplainsFailure(read.Snapshots) && read.FailureSequence < failureSequence)
            {
                // The shared read started before the AppHost failed this wait, so it can predate the exit.
                read = await monitor.ReadSnapshotsAsync(failureSequence, acceptEarlierRead: false, resolutionToken).ConfigureAwait(false);
            }

            snapshots = read.Snapshots;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                "Unable to read the exit code of resource '{ResourceName}' after a failed wait: {ExceptionType}",
                result.ResourceName,
                ex.GetType().Name);
            return result;
        }

        if (FindResourceSnapshots(snapshots, result.ResourceName) is [var readSnapshot])
        {
            if (IsExited(readSnapshot))
            {
                return ApplyExit(result, readSnapshot.State, readSnapshot.HealthStatus, readSnapshot.ExitCode);
            }

            // The read is stored as the latest snapshot unless the watch already has a newer one.
            if (IsPendingExit(readSnapshot))
            {
                return await WaitForExitCodeAsync(result, monitor, resolutionToken).ConfigureAwait(false);
            }
        }

        return result;

        bool ExplainsFailure(IReadOnlyList<ResourceSnapshot> readSnapshots)
        {
            // Several matches mean the AppHost rejected an ambiguous name, which no newer read can change.
            var matches = FindResourceSnapshots(readSnapshots, result.ResourceName);
            return matches.Count > 1 || matches is [var match] && IsStoppedWaitState(match);
        }
    }

    /// <summary>
    /// Waits on the snapshot watch for the exit code of a resource that stopped without one.
    /// </summary>
    /// <remarks>
    /// An unknown exit code must neither complete the resource nor fail the batch, so this waits until the code
    /// arrives or the batch's resolution is cancelled by the deadline or another resource's failure. The AppHost
    /// does not resend a snapshot that was already published, so once the watch ended the code cannot arrive and
    /// the wait is reported as timed out.
    /// </remarks>
    private static async Task<ResourceWaitResult> WaitForExitCodeAsync(
        ResourceWaitResult result,
        BatchSnapshotMonitor monitor,
        CancellationToken resolutionToken)
    {
        while (true)
        {
            resolutionToken.ThrowIfCancellationRequested();
            var view = monitor.Find(result.ResourceName);
            if (view.Matches is not [var snapshot])
            {
                return result;
            }

            if (IsExited(snapshot))
            {
                return ApplyExit(result, snapshot.State, snapshot.HealthStatus, snapshot.ExitCode);
            }

            // A resource that started again no longer explains the AppHost's failure with an exit.
            if (!IsPendingExit(snapshot))
            {
                return result;
            }

            if (view.WatchEnded)
            {
                return ApplyPendingExit(result, snapshot.State, snapshot.HealthStatus, batchFailed: false);
            }

            await view.Changed.WaitAsync(resolutionToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Settles a failed wait whose completion resolution was cancelled by the deadline or by another resource's failure.
    /// </summary>
    private static ResourceWaitResult ApplyInterruptedResolution(ResourceWaitResult result, BatchSnapshotMonitor monitor, bool batchFailed)
    {
        var view = monitor.Find(result.ResourceName);
        var latest = FindExplainingSnapshot(result, view.Matches, view.HasReadSnapshots);
        if (latest is not null && IsExited(latest))
        {
            return ApplyExit(result, latest.State, latest.HealthStatus, latest.ExitCode);
        }

        // The AppHost's response can report the stop before the watch shows it. Either way, a stop without an
        // exit code is not known to be a failure, so it must not fail the batch.
        if (IsPendingExit(result.State, result.ExitCode) || latest is not null && IsPendingExit(latest))
        {
            return ApplyPendingExit(result, latest?.State ?? result.State, latest?.HealthStatus ?? result.Health, batchFailed);
        }

        return result;
    }

    /// <summary>
    /// Returns the snapshot that can explain a failed wait, or <see langword="null"/> when the batch's view of the
    /// application cannot.
    /// </summary>
    private static ResourceSnapshot? FindExplainingSnapshot(ResourceWaitResult result, IReadOnlyList<ResourceSnapshot> matches, bool hasReadSnapshots)
    {
        if (matches is not [var snapshot])
        {
            return null;
        }

        // Older AppHosts answer a display name shared by several replicas like a healthy wait that stopped on the
        // resource: with an error and no state. The watch replays the application one snapshot at a time, so until
        // the snapshots were read, a single replica found by display name does not show that the name is unique.
        if (result.State is null &&
            !hasReadSnapshots &&
            !string.Equals(snapshot.Name, result.ResourceName, StringComparisons.ResourceName))
        {
            return null;
        }

        return snapshot;
    }

    private static ResourceWaitResult ApplyExit(ResourceWaitResult result, string? state, string? health, int? exitCode)
    {
        if (IsSuccessfulCompletion(state, exitCode))
        {
            return result with
            {
                Outcome = ResourceWaitOutcome.Completed,
                State = state,
                Health = health,
                ErrorMessage = null,
                ExitCode = 0
            };
        }

        // Surface the state so callers can explain the failure, for example "Exited (exit code: 1)".
        // Older AppHosts do not return a state when the healthy wait stops early.
        return result with
        {
            Outcome = ResourceWaitOutcome.Failure,
            State = state,
            Health = health,
            ExitCode = exitCode
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
        return IsSuccessfulCompletion(snapshot.State, snapshot.ExitCode);
    }

    private static bool IsSuccessfulCompletion(string? state, int? exitCode)
    {
        return exitCode == 0 &&
            (string.Equals(state, KnownResourceStates.Finished, StringComparisons.ResourceState) ||
             string.Equals(state, KnownResourceStates.Exited, StringComparisons.ResourceState));
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
        // The AppHost's timeout only starts once the request arrives and only it can end the call, so also bound
        // the call locally (see s_appHostReplyGracePeriod).
        using var replyDeadlineCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(timeoutSeconds) + s_appHostReplyGracePeriod,
            timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, replyDeadlineCts.Token);

        WaitForResourceResponse response;
        try
        {
            response = await connection.WaitForResourceAsync(
                resourceName,
                GetProtocolValue(target),
                timeoutSeconds,
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Surface the caller's token rather than the linked one, so callers can recognize their own cancellation.
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (OperationCanceledException) when (replyDeadlineCts.IsCancellationRequested)
        {
            logger.LogDebug(
                "The AppHost did not answer the wait for resource '{ResourceName}' within its timeout",
                resourceName);
            return new ResourceWaitResult(
                ResourceWaitOutcome.Timeout,
                resourceName,
                State: null,
                Health: null,
                ResourceNotFound: false,
                ErrorMessage: null,
                timeProvider.GetElapsedTime(startTimestamp));
        }

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
            timeProvider.GetElapsedTime(startTimestamp))
        {
            ExitCode = response.ExitCode,
            ResourceAmbiguous = response.ResourceAmbiguous
        };
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

    /// <param name="Matches">The latest snapshots that match the requested name.</param>
    /// <param name="Changed">Completes on the next change.</param>
    /// <param name="WatchEnded">Whether the watch ended, so no further change can arrive.</param>
    /// <param name="HasReadSnapshots">Whether the snapshots were read, so every resource is known.</param>
    private readonly record struct SnapshotView(IReadOnlyList<ResourceSnapshot> Matches, Task Changed, bool WatchEnded, bool HasReadSnapshots);

    /// <summary>
    /// The result of a shared snapshot read, with the failure sequence when the read started. A read can only
    /// explain failures whose sequence it covers, because the AppHost publishes a stopped state before failing a wait.
    /// </summary>
    private readonly record struct SnapshotRead(IReadOnlyList<ResourceSnapshot> Snapshots, long FailureSequence);

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
        private bool _hasReadSnapshots;
        private long _failureSequence;
        private Task<SnapshotRead>? _read;
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
                // A stale replay must not record an exit: the resource may have restarted since, and its exit
                // would fail a batch in which the resource is running.
                if (!StoreLatest(snapshot))
                {
                    return false;
                }

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
                return new SnapshotView(FindResourceSnapshots(_latest.Values.ToArray(), resourceName), _changed.Task, _watchEnded, _hasReadSnapshots);
            }
        }

        /// <summary>
        /// Gets whether the snapshots were read, so the latest snapshots include every resource.
        /// </summary>
        public bool HasReadSnapshots
        {
            get
            {
                lock (_lock)
                {
                    return _hasReadSnapshots;
                }
            }
        }

        /// <summary>
        /// Records snapshots read from the AppHost like snapshots from the watch, and returns whether any of them is a
        /// failed exit of a waited resource.
        /// </summary>
        public bool ObserveCurrentSnapshots(IReadOnlyList<ResourceSnapshot> snapshots)
        {
            lock (_lock)
            {
                _hasReadSnapshots = true;
            }

            var failedExit = false;
            foreach (var snapshot in snapshots)
            {
                failedExit |= Observe(snapshot);
            }

            return failedExit;
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
        /// <param name="failureSequence">The sequence returned by <see cref="MarkFailureObserved"/> for the caller's failure.</param>
        /// <param name="acceptEarlierRead">
        /// Whether to use the latest read, pending or completed, even if it started before the caller's failure.
        /// Sharing it keeps a burst of failures to a single read; the caller only needs a newer read when the
        /// shared one does not explain its failure.
        /// </param>
        /// <param name="cancellationToken">The batch's resolution token, shared by every caller.</param>
        public Task<SnapshotRead> ReadSnapshotsAsync(long failureSequence, bool acceptEarlierRead, CancellationToken cancellationToken)
        {
            Task<SnapshotRead> read;
            lock (_lock)
            {
                // A read that started before this failure was observed can predate the snapshot that explains it.
                if (_read is { IsFaulted: false, IsCanceled: false } existing &&
                    (acceptEarlierRead || _readSequence >= failureSequence))
                {
                    read = existing;
                }
                else
                {
                    _readSequence = Interlocked.Read(ref _failureSequence);
                    read = _read = ReadAndStoreAsync(_readSequence, cancellationToken);
                }
            }

            return read.WaitAsync(cancellationToken);
        }

        private async Task<SnapshotRead> ReadAndStoreAsync(long failureSequence, CancellationToken cancellationToken)
        {
            var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);

            // Keep reads for the batch's final view of the application, which matters when the watch ended early.
            // Reads do not record exits: they only resolve failures the AppHost already reported.
            lock (_lock)
            {
                _hasReadSnapshots = true;
                foreach (var snapshot in snapshots)
                {
                    StoreLatest(snapshot);
                }
            }

            return new SnapshotRead(snapshots, failureSequence);
        }

        /// <summary>
        /// Stores <paramref name="snapshot"/> unless a newer snapshot of the same resource is already known,
        /// and returns whether it was stored.
        /// </summary>
        private bool StoreLatest(ResourceSnapshot snapshot)
        {
            // Version 0 comes from AppHosts that do not version snapshots and cannot establish ordering.
            // Otherwise a lower version is a stale replay that must not replace a newer snapshot.
            if (_latest.TryGetValue(snapshot.Name, out var current) &&
                snapshot.Version > 0 &&
                current.Version > 0 &&
                snapshot.Version < current.Version)
            {
                return false;
            }

            _latest[snapshot.Name] = snapshot;
            return true;
        }
    }
}
