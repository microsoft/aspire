// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// Shared "graceful signal → bounded wait → force tree-kill → bounded drain" ladder used by
/// every long-running child process the CLI owns during <c>aspire run</c>
/// (AppHost server, guest, direct-launch AppHost executable). Each call site provides a
/// graceful signaler and the central <see cref="GracefulShutdownService.Token"/>; this helper
/// runs the same four-phase escalation against them so the user-visible shutdown shape is
/// uniform across spawn sites.
/// </summary>
/// <remarks>
/// Whoever triggers shutdown (<see cref="ConsoleCancellationManager.Cancel"/>) is responsible
/// for starting the central clock. This helper only consumes the resulting token — it never
/// owns timing.
/// </remarks>
internal static class ProcessGracefulShutdownLadder
{
    /// <summary>
    /// Runs the four-phase shutdown ladder against <paramref name="process"/>.
    /// </summary>
    /// <param name="process">The child process to shut down.</param>
    /// <param name="signaler">Issues the graceful signal (DCP <c>stop-process-tree</c> on Windows, SIGTERM on Unix).</param>
    /// <param name="gracefulToken">The central <see cref="GracefulShutdownService.Token"/>.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="processDescription">Short human description used in log messages (e.g. <c>"AppHost server"</c>).</param>
    public static async Task ExecuteAsync(
        Process process,
        IProcessTreeGracefulShutdownSignaler signaler,
        CancellationToken gracefulToken,
        ILogger logger,
        string processDescription)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(signaler);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processDescription);

        // Phase 1: best-effort graceful signal bounded by the central token. The DCP path on
        // Windows shells out (layout discovery + DCP process launch + wait) and could consume
        // the entire graceful window before we ever reach WaitForExitAsync. Sharing the central
        // token means a 2nd-Ctrl+C Expire() interrupts the slow DCP shell-out exactly the same
        // way it interrupts the WaitForExitAsync below.
        try
        {
            // startTime is intentionally null: includeStartTimeForDcp is always false at this
            // call site (the Unix branch ignores StartTime entirely; the Windows DCP branch
            // only consults it when includeStartTimeForDcp is true). Querying Process.StartTime
            // here would just risk an InvalidOperationException on a process whose handle has
            // been closed or is in a state that disallows the read.
            await signaler.RequestProcessTreeGracefulShutdownAsync(
                process.Id,
                startTime: null,
                includeStartTimeForDcp: false,
                gracefulToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gracefulToken.IsCancellationRequested)
        {
            // Graceful budget expired before the signal could be issued; fall through to kill.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to issue graceful shutdown to {ProcessDescription} (pid {Pid}); escalating to kill.", processDescription, SafePid(process));
        }

        // Phase 2: wait for exit bounded by the same central token. Whoever initiated shutdown
        // (user Ctrl+C via CCM.Cancel) already started the clock; we only consume the token.
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
