// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/// <summary>
/// Watches a parent process (by PID plus start time) and invokes a caller-supplied action once when that
/// parent is no longer running. Shared by the CLI's launcher monitor and the <c>aspire-managed</c> parent
/// watchdog so both get the same poll loop and teardown semantics.
/// </summary>
internal sealed class ParentProcessLivenessMonitor : IAsyncDisposable
{
    // The poll interval is intentionally coarse: this is a backstop for a parent that died abnormally, so
    // a one-second granularity is plenty and keeps the wakeups cheap.
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _monitorTask;
    private int _disposed;

    private ParentProcessLivenessMonitor(
        int parentPid,
        long? parentStartedUnixSeconds,
        Func<CancellationToken, Task> onParentExited,
        TimeProvider timeProvider,
        bool useRuntimeStartTime)
    {
        _monitorTask = MonitorAsync(parentPid, parentStartedUnixSeconds, onParentExited, timeProvider, useRuntimeStartTime, _stopCts.Token);
    }

    /// <summary>
    /// Starts polling the parent identified by <paramref name="parentPid"/> (and, when supplied,
    /// <paramref name="parentStartedUnixSeconds"/> to guard against PID reuse). The parent is probed once
    /// immediately, then on every poll interval, so a parent that is already gone is detected without
    /// waiting a full tick. When the parent is gone, <paramref name="onParentExited"/> is invoked exactly
    /// once. The token passed to that callback is cancelled when this monitor is disposed, so any grace
    /// delay inside the callback is unwound if the caller disarms first.
    /// </summary>
    public static ParentProcessLivenessMonitor Start(
        int parentPid,
        long? parentStartedUnixSeconds,
        Func<CancellationToken, Task> onParentExited,
        TimeProvider? timeProvider = null,
        bool useRuntimeStartTime = false)
    {
        return new ParentProcessLivenessMonitor(parentPid, parentStartedUnixSeconds, onParentExited, timeProvider ?? TimeProvider.System, useRuntimeStartTime);
    }

    private static async Task MonitorAsync(
        int parentPid,
        long? parentStartedUnixSeconds,
        Func<CancellationToken, Task> onParentExited,
        TimeProvider timeProvider,
        bool useRuntimeStartTime,
        CancellationToken stopToken)
    {
        try
        {
            // Hop off the caller's thread so Start() stays non-blocking (the original first operation was
            // an async timer await). The probe below then still runs on the very next scheduler turn
            // instead of after a full poll interval.
            await Task.Yield();

            using var timer = new PeriodicTimer(s_pollInterval, timeProvider);

            // Probe once before awaiting the first tick. If the parent already died during our startup
            // window it is detected immediately instead of after a full poll interval, which closes the
            // gap where the parent is gone before the watchdog notices. This matches CliOrphanDetector,
            // whose do/while loop also checks once before it awaits its timer. See
            // src/Aspire.Hosting/Cli/CliOrphanDetector.cs.
            do
            {
                // Honor a disarm that raced with this probe: a disposed monitor must never invoke the
                // callback. The original loop guaranteed this by awaiting the (already-cancelled) timer
                // first; ThrowIfCancellationRequested preserves that when we probe before the timer.
                stopToken.ThrowIfCancellationRequested();

                var isProcessRunning = useRuntimeStartTime && parentStartedUnixSeconds is { } legacyStartTime
                    ? ProcessStartTimeHelper.IsProcessRunningWithRuntimeStartTime(parentPid, legacyStartTime, ProcessStartTimeHelper.CrossProcessIdentityTimeTolerance)
                    : ProcessStartTimeHelper.IsProcessRunning(parentPid, parentStartedUnixSeconds);
                if (isProcessRunning)
                {
                    continue;
                }

                // Pass stopToken through so a callback that waits (e.g. a force-exit grace period) is
                // cancelled if the caller disposes the monitor while the callback is running.
                try
                {
                    await onParentExited(stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // The callback is only a best-effort cleanup hook after the parent is gone. Don't
                    // let callback failures fault monitor disposal paths that are already unwinding.
                }

                return;
            } while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Disarmed (stopped/disposed) before the parent exited — the expected, common case.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: callers may dispose on a happy path and again from an outer finally backstop.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopCts.Cancel();
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping the monitor loop.
        }
        finally
        {
            _stopCts.Dispose();
        }
    }
}
