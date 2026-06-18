// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// The single child-process shutdown helper for every long-running process the CLI owns. It has two
/// modes selected by whether a graceful signaler is supplied:
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Graceful</b> (signaler supplied — the <c>aspire run</c> path): runs the
///       "graceful signal → bounded wait → force tree-kill → bounded drain" four-phase escalation
///       against the central <see cref="ConsoleCancellationManager.GracefulShutdownToken"/>, so the
///       user-visible shutdown shape is uniform across spawn sites (AppHost server, guest,
///       direct-launch AppHost executable).
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Force</b> (no signaler — every non-Run caller: build, restore, package add, layout, the
///       <c>aspire stop</c> force-kill tail, etc.): best-effort courtesy SIGTERM on Unix (a no-op on
///       Windows, where Ctrl+C delivery needs the signaler-backed DCP console dance) followed by an
///       immediate force-kill. There is no graceful budget on this path.
///     </description>
///   </item>
/// </list>
/// Both modes use the same primitives the previous <c>ProcessTerminator</c> / ladder split used —
/// DCP <c>stop-process-tree</c> on Windows and SIGTERM on Unix — they only differ in whether a
/// graceful budget is honored before the kill.
/// </summary>
/// <remarks>
/// Whoever triggers shutdown (<see cref="ConsoleCancellationManager.Cancel"/>) is responsible
/// for starting the central clock. This helper only consumes the resulting token — it never
/// owns timing.
/// </remarks>
internal static class ProcessGracefulShutdownLadder
{
    /// <summary>
    /// Shuts down <paramref name="process"/>, choosing the graceful ladder or the force-kill fallback
    /// based on whether <paramref name="signaler"/> is supplied.
    /// </summary>
    /// <param name="process">The child process to shut down.</param>
    /// <param name="signaler">
    /// Issues the graceful signal (DCP <c>stop-process-tree</c> on Windows, SIGTERM on Unix). When
    /// <c>null</c>, the force-kill fallback runs instead and <paramref name="gracefulToken"/> is ignored.
    /// </param>
    /// <param name="gracefulToken">
    /// The central <see cref="ConsoleCancellationManager.GracefulShutdownToken"/> bounding the graceful
    /// wait. Only consulted when <paramref name="signaler"/> is non-null.
    /// </param>
    /// <param name="entireProcessTreeOnForceKill">
    /// Kill scope for the <em>force-kill fallback</em> (used only when <paramref name="signaler"/> is
    /// <c>null</c>). The graceful escalation always tree-kills regardless of this value, because a child
    /// like tsx can swallow Ctrl+C and leave descendants running even after a clean graceful signal.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="processDescription">Short human description used in log messages (e.g. <c>"AppHost server"</c>).</param>
    public static async Task ShutdownAsync(
        Process process,
        IProcessTreeGracefulShutdownSignaler? signaler,
        CancellationToken gracefulToken,
        bool entireProcessTreeOnForceKill,
        ILogger logger,
        string processDescription)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processDescription);

        if (signaler is null)
        {
            // Force mode: no graceful budget. Best-effort courtesy SIGTERM (Unix) then hard-kill.
            ForceKill(process, entireProcessTreeOnForceKill, logger, processDescription);
            return;
        }

        // Phase 1: fire-and-forget the graceful signal so its wait does not consume the
        // graceful budget. On Windows, DCP's `stop-process-tree` delivers the Ctrl+C signal
        // synchronously (in milliseconds) and then BLOCKS until the target process actually
        // exits. Awaiting it sequentially would burn the entire graceful window inside DCP's
        // wait, leaving zero time for Phase 2's WaitForExitAsync — which then forces a
        // tree-kill at the budget boundary even when the AppHost was milliseconds away from
        // exiting cleanly. By running the signaler in parallel, the apphost receives the
        // signal immediately AND the full graceful budget is allocated to actual exit-wait.
        // Important: the signaler is invoked unconditionally (not gated on the graceful
        // token) so that when the token is already cancelled at ladder-entry the signal
        // still goes out — callers like `aspire stop` Expire() the budget intentionally
        // and rely on the signal still being dispatched best-effort.
        var signalTask = InvokeSignalerAsync(signaler, SafePid(process), gracefulToken, processDescription, logger);

        // Phase 2: wait for exit with the FULL graceful budget. When the apphost exits,
        // the signaler task observes the same exit and completes shortly after. Whoever
        // triggered shutdown (CCM.Cancel) owns the timing of `gracefulToken`.
        try
        {
            await process.WaitForExitAsync(gracefulToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Graceful budget expired; fall through to kill.
        }

        if (process.HasExited)
        {
            // Best-effort: drain the signaler so its dcp shell-out doesn't outlive us as
            // an orphan; safe because the process has already exited so dcp will return
            // promptly. Bounded so a stuck dcp can't keep us pinned.
            try
            {
                using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await signalTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
            return;
        }

        // Phase 3: ALWAYS tree-kill on escalation, regardless of OS. Even when the graceful
        // signal returned cleanly, descendants may still be alive — e.g. on Windows tsx wraps
        // node and swallows Ctrl+C/Ctrl+Break, leaving the child node and any further
        // descendants running after the tsx shell exits. Skipping tree-kill would orphan them.
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process exited between HasExited check and Kill — nothing to do.
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to kill {ProcessDescription} (pid {Pid}).", processDescription, SafePid(process));
            return;
        }

        // Phase 4: brief separately-bounded drain after kill — independent of the central token
        // because by now the central budget has already expired. 1 s is enough for the OS to
        // reap the process so the subsequent ExitCode read succeeds.
        try
        {
            using var killDrain = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await process.WaitForExitAsync(killDrain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort; nothing more we can do.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error draining killed {ProcessDescription} (pid {Pid}).", processDescription, SafePid(process));
        }
    }

    private static async Task InvokeSignalerAsync(
        IProcessTreeGracefulShutdownSignaler signaler,
        int pid,
        CancellationToken gracefulToken,
        string processDescription,
        ILogger logger)
    {
        try
        {
            // startTime is intentionally null: includeStartTimeForDcp is always false at this
            // call site (the Unix branch ignores StartTime entirely; the Windows DCP branch
            // only consults it when includeStartTimeForDcp is true). Querying Process.StartTime
            // here would just risk an InvalidOperationException on a process whose handle has
            // been closed or is in a state that disallows the read.
            //
            // Yield onto the thread pool so we don't block the caller while the signaler
            // performs its (sometimes slow) work — DCP's stop-process-tree blocks until the
            // target process actually exits, which is exactly the wait we want to avoid
            // serializing in front of Phase 2's WaitForExitAsync.
            await Task.Yield();

            await signaler.RequestProcessTreeGracefulShutdownAsync(
                pid,
                startTime: null,
                includeStartTimeForDcp: false,
                gracefulToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gracefulToken.IsCancellationRequested)
        {
            // Graceful budget expired before the signal could be issued; the kill path
            // is responsible for terminating the process.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to issue graceful shutdown to {ProcessDescription} (pid {Pid}); escalating to kill.", processDescription, pid);
        }
    }

    private static void ForceKill(Process process, bool entireProcessTree, ILogger logger, string processDescription)
    {
        // Mirrors the previous ProcessTerminator force path: resolve "already gone?", issue a
        // best-effort courtesy SIGTERM on Unix (so a SIGTERM-aware child can flush), then hard-kill.
        // On Windows ProcessSignaler.RequestGracefulShutdown is a no-op — Ctrl+C delivery to a child
        // requires DCP's stop-process-tree console dance, which only the signaler-backed graceful
        // ladder performs — so we skip straight to the kill.
        try
        {
            if (process.HasExited)
            {
                logger.LogDebug("{ProcessDescription} process {ProcessId} already exited.", processDescription, process.Id);
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                ProcessSignaler.RequestGracefulShutdown(process.Id, expectedStartTime: null, logger);

                if (process.HasExited)
                {
                    return;
                }
            }

            logger.LogDebug(
                "Sending kill to {ProcessDescription} process {ProcessId} (entireProcessTree={EntireProcessTree}).",
                processDescription,
                process.Id,
                entireProcessTree);
            process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(
                ex,
                "{ProcessDescription} process exited before termination could complete (entireProcessTree={EntireProcessTree}).",
                processDescription,
                entireProcessTree);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to terminate {ProcessDescription} process (entireProcessTree={EntireProcessTree}).",
                processDescription,
                entireProcessTree);
        }
    }

    private static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
