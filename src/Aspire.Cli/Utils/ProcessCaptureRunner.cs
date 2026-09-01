// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

internal static class ProcessCaptureRunner
{
    // Maximum time we'll wait for a process to actually exit after TryKillProcessTree
    // returns. Kill is best-effort: it can fail silently (perm denied, job-object
    // reparenting on Windows), and in those cases an unbounded WaitForExitAsync
    // would deadlock the caller indefinitely even though we've already decided to
    // abandon the process. 2s is well above the <100ms typical post-kill exit
    // latency but small enough not to noticeably stall the caller.
    private static readonly TimeSpan s_postKillExitWaitBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_postKillCaptureWaitBound = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_postExitCaptureWaitBound = TimeSpan.FromSeconds(2);

    public static async Task<ProcessCaptureResult<TCapture>> RunAsync<TCapture>(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        Func<Process, CancellationToken, Task<TCapture>> captureAsync,
        Func<TCapture> createEmptyCapture,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(captureAsync);
        ArgumentNullException.ThrowIfNull(createEmptyCapture);
        ArgumentNullException.ThrowIfNull(logger);

        Process process;
        try
        {
            var started = Process.Start(startInfo);
            if (started is null)
            {
                return new ProcessCaptureResult<TCapture>(
                    ExitCode: -1,
                    Capture: createEmptyCapture(),
                    FailureKind: ProcessCaptureFailureKind.StartFailed,
                    FailureMessage: "Process.Start returned null.",
                    Cancelled: false);
            }

            process = started;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "Could not start process '{FileName}'.", startInfo.FileName);
            return new ProcessCaptureResult<TCapture>(
                ExitCode: -1,
                Capture: createEmptyCapture(),
                FailureKind: ProcessCaptureFailureKind.StartFailed,
                FailureMessage: ex.Message,
                Cancelled: false);
        }

        // Once the process has been started we OWN it. The finally below
        // guarantees we kill any still-running process and dispose the handle
        // even if the surrounding code throws an unexpected exception (for
        // example InvalidOperationException from WaitForExitAsync when the
        // process handle becomes invalid mid-wait, or an IOException from the
        // underlying wait primitive). Without the try/finally, those rare
        // exception paths would propagate out leaving the peer alive and the
        // Process object undisposed — violating the file-level contract that
        // no spawned peer outlives the parent.
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            Task<TCapture> captureTask;
            try
            {
                captureTask = captureAsync(process, timeoutCts.Token);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not start capturing output for process '{FileName}'.", startInfo.FileName);
                TryKillProcessTree(process, logger);
                await SwallowExitWaitAsync(process, s_postKillExitWaitBound, logger).ConfigureAwait(false);
                return new ProcessCaptureResult<TCapture>(
                    ExitCode: -1,
                    Capture: createEmptyCapture(),
                    FailureKind: ProcessCaptureFailureKind.CaptureFailed,
                    FailureMessage: ex.Message,
                    Cancelled: false);
            }

            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            // Any other exception (e.g. InvalidOperationException from a torn-down
            // handle, IOException from the wait primitive) is left to propagate so
            // the outer finally still runs the kill + dispose path.

            if (timedOut || cancelled)
            {
                TryKillProcessTree(process, logger);
                await SwallowExitWaitAsync(process, s_postKillExitWaitBound, logger).ConfigureAwait(false);
                var interruptedCapture = await SwallowCaptureAsync(captureTask, createEmptyCapture, logger, s_postKillCaptureWaitBound).ConfigureAwait(false);

                // Drive the capture task to completion if the bounded wait gave up on it.
                // The capture task observes timeoutCts.Token, which is already cancelled in
                // the timeout branch but not in the user-cancellation branch when the
                // outer cancellationToken cancels before timeoutCts fires. Signalling here
                // unifies both paths and ensures no capture task outlives this method's
                // return — disposing the CTS in the surrounding `using` does NOT cancel it.
                timeoutCts.Cancel();

                return new ProcessCaptureResult<TCapture>(
                    ExitCode: -1,
                    Capture: interruptedCapture,
                    FailureKind: timedOut ? ProcessCaptureFailureKind.TimedOut : null,
                    FailureMessage: timedOut ? $"Process timed out after {timeout.TotalSeconds:F1}s." : null,
                    Cancelled: cancelled);
            }

            // The process exited cleanly under the timeout, but the capture task may
            // still be reading. The pipes normally close on child exit and the readers
            // EOF immediately; however, a child that left descendants holding inherited
            // stdout/stderr handles keeps them open. timeoutCts is still ticking, so an
            // unbounded await here would block up to the remaining wall-clock timeout
            // budget (potentially several seconds for a peer that exited in
            // milliseconds). Cap the post-exit drain, but allow more time than the
            // post-kill path: the peer has already exited normally, so losing recently
            // emitted stdout/stderr would make diagnostics flaky under CI load.
            var capture = await SwallowCaptureAsync(captureTask, createEmptyCapture, logger, s_postExitCaptureWaitBound).ConfigureAwait(false);
            var exitCode = process.ExitCode;

            // If the bounded drain timed out (pipes inherited by descendants), the
            // capture task is still awaiting on timeoutCts.Token. Disposing the CTS in
            // the `using` does NOT cancel it, so without an explicit Cancel here the
            // task could linger up to the remaining wall-clock timeout budget after we
            // return, holding inherited stdout/stderr handles open for that long.
            // Cancelling drives the read to terminate promptly.
            timeoutCts.Cancel();

            return new ProcessCaptureResult<TCapture>(
                ExitCode: exitCode,
                Capture: capture,
                FailureKind: null,
                FailureMessage: null,
                Cancelled: false);
        }
        finally
        {
            // Belt-and-suspenders: kill any still-running process before
            // disposing. TryKillProcessTree is a no-op once HasExited is true,
            // so the happy path costs only a quick property read.
            TryKillProcessTree(process, logger);
            process.Dispose();
        }
    }

    private static void TryKillProcessTree(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Could not kill process {Pid}.", TryGetPid(process));
        }
    }

    private static async Task SwallowExitWaitAsync(Process process, TimeSpan bound, ILogger logger)
    {
        // Bounded post-kill wait: if TryKillProcessTree silently failed and the
        // process is still alive past `bound`, abandon rather than block the
        // caller indefinitely. Logged at debug so an operator chasing a hung
        // peer can see that the process outlived its termination request.
        using var cts = new CancellationTokenSource(bound);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            logger.LogDebug(
                "Process {Pid} did not exit within {Bound}s after kill request; abandoning.",
                TryGetPid(process), bound.TotalSeconds);
        }
        catch
        {
            // Already being torn down; failed wait is not actionable for the caller.
        }
    }

    private static int TryGetPid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static async Task<TCapture> SwallowCaptureAsync<TCapture>(Task<TCapture> task, Func<TCapture> createEmptyCapture, ILogger logger, TimeSpan? bound = null)
    {
        try
        {
            if (bound is { } waitBound)
            {
                return await task.WaitAsync(waitBound).ConfigureAwait(false);
            }

            return await task.ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            logger.LogDebug(ex, "Timed out waiting {Bound}s for process output capture.", bound?.TotalSeconds);
            ObserveCaptureFault(task);
            return createEmptyCapture();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not capture process output.");
            return createEmptyCapture();
        }
    }

    private static void ObserveCaptureFault<TCapture>(Task<TCapture> task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal readonly record struct ProcessCaptureResult<TCapture>(
    int ExitCode,
    TCapture Capture,
    ProcessCaptureFailureKind? FailureKind,
    string? FailureMessage,
    bool Cancelled);

internal enum ProcessCaptureFailureKind
{
    StartFailed,
    CaptureFailed,
    TimedOut,
}
