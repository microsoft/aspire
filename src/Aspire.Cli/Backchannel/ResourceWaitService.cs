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
/// Applies the shared Aspire backchannel wait semantics for a resource.
/// </summary>
internal sealed class ResourceWaitService(
    TimeProvider timeProvider,
    ILogger<ResourceWaitService> logger)
{
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
    public async Task<IReadOnlyList<ResourceWaitResult>> WaitUntilDoneAsync(
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

        // A "down" wait is satisfied by any exit, so exits only need watching for running targets.
        var observedExits = new ResourceSnapshot?[resourceNames.Count];
        var watchTask = target is ResourceWaitTarget.Down
            ? Task.CompletedTask
            : WatchForExitsAsync(connection, resourceNames, observedExits, failFastCts, watchCts.Token);

        ResourceWaitResult[] results;
        try
        {
            var waitTasks = resourceNames
                .Select(resourceName => WaitUntilDoneCoreAsync(
                    connection,
                    resourceName,
                    target,
                    startTimestamp,
                    timeout,
                    failFastCts,
                    cancellationToken))
                .ToArray();

            results = await Task.WhenAll(waitTasks).ConfigureAwait(false);
        }
        finally
        {
            await watchCts.CancelAsync().ConfigureAwait(false);
            await watchTask.ConfigureAwait(false);
        }

        // Read the observations only after the watcher has completed.
        return results
            .Select((result, index) => ApplyObservedExit(result, observedExits[index]))
            .ToArray();
    }

    private async Task WatchForExitsAsync(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<string> resourceNames,
        ResourceSnapshot?[] observedExits,
        CancellationTokenSource failFastCts,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in connection.WatchResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false))
            {
                if (!IsExited(snapshot))
                {
                    continue;
                }

                for (var i = 0; i < resourceNames.Count; i++)
                {
                    // Wait requests accept a runtime name or a display name shared by all replicas.
                    if (!string.Equals(snapshot.Name, resourceNames[i], StringComparisons.ResourceName) &&
                        !string.Equals(snapshot.DisplayName, resourceNames[i], StringComparisons.ResourceName))
                    {
                        continue;
                    }

                    // Keep a failed replica's exit once seen so a sibling replica that exits with 0 cannot hide it.
                    if (observedExits[i] is not { } previous || !IsFailedExit(previous))
                    {
                        observedExits[i] = snapshot;
                    }

                    if (IsFailedExit(snapshot))
                    {
                        await failFastCts.CancelAsync().ConfigureAwait(false);
                    }
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
        return IsTerminalFailureState(snapshot.State) ||
            (snapshot.ExitCode is not null &&
             (string.Equals(snapshot.State, KnownResourceStates.Finished, StringComparisons.ResourceState) ||
              string.Equals(snapshot.State, KnownResourceStates.Exited, StringComparisons.ResourceState)));
    }

    private static bool IsFailedExit(ResourceSnapshot snapshot)
    {
        return IsExited(snapshot) && !IsSuccessfulCompletion(snapshot);
    }

    private async Task<ResourceWaitResult> WaitUntilDoneCoreAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        long startTimestamp,
        TimeSpan timeout,
        CancellationTokenSource failFastCts,
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
            return new ResourceWaitResult(
                ResourceWaitOutcome.Cancelled,
                resourceName,
                State: null,
                Health: null,
                ResourceNotFound: false,
                ErrorMessage: null,
                timeProvider.GetElapsedTime(resourceStartTimestamp));
        }

        // A "down" wait already succeeds for any exit code, so only running targets need to
        // distinguish a successful run-to-completion from a crash.
        if (result.Outcome == ResourceWaitOutcome.Failure &&
            !result.ResourceNotFound &&
            target is not ResourceWaitTarget.Down)
        {
            result = await ResolveCompletionAsync(connection, result, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == ResourceWaitOutcome.Failure)
        {
            await failFastCts.CancelAsync().ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ResourceWaitResult> ResolveCompletionAsync(
        IAppHostAuxiliaryBackchannel connection,
        ResourceWaitResult result,
        CancellationToken cancellationToken)
    {
        List<ResourceSnapshot> snapshots;
        try
        {
            snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                "Unable to read the exit code of resource '{ResourceName}' after a failed wait: {ExceptionType}",
                result.ResourceName,
                ex.GetType().Name);
            return result;
        }

        // The AppHost rejects a display name shared by several replicas before waiting, and other
        // failures (an AppHost without wait support, RPC errors) are not about the resource's state.
        // Only a single resource that actually exited can explain the failure; otherwise keep the
        // original error message.
        var matches = FindResourceSnapshots(snapshots, result.ResourceName);
        if (matches is not [var snapshot] || !IsExited(snapshot))
        {
            return result;
        }

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
}
