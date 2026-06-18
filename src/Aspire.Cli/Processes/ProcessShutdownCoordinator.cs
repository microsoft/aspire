// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// The single decision point every CLI process owner routes through when a child must be torn
/// down on cancellation. Picks between the shared <see cref="ProcessGracefulShutdownLadder"/>
/// (graceful signal → bounded wait → tree-kill, used on the <c>aspire run</c> path) and the
/// best-effort <see cref="ProcessTerminator"/> force-kill fallback (non-Run callers, or the
/// dispose-only path where no central graceful budget was ever started).
/// </summary>
/// <remarks>
/// This consolidates the "ladder vs. force-kill" branch that previously lived, copy-pasted and
/// drifted, in <c>ProcessExecution</c>, <c>IsolatedProcessExecution</c>, <c>AppHostServerSession</c>
/// and <c>ProcessGuestLauncher</c>. Having one place means the choice — and the fallback's exact
/// semantics — can only be defined once.
/// </remarks>
internal static class ProcessShutdownCoordinator
{
    // Pre-cancelled token handed to the force-kill fallback's graceful wait. An already-cancelled
    // token makes ProcessTerminator dispatch the best-effort SIGTERM (Unix) and then immediately
    // escalate to Kill, rather than waiting for a graceful exit. CancellationToken.None must NOT
    // be used here: with requestGracefulShutdown the terminator would WaitForExitAsync(None) and
    // block forever if the child ignores SIGTERM.
    private static readonly CancellationToken s_immediateEscalation = new(canceled: true);

    /// <summary>
    /// Tears down <paramref name="process"/> on cancellation, running the graceful ladder when the
    /// run-path graceful infrastructure is wired and its budget is live, otherwise force-killing.
    /// </summary>
    /// <param name="process">The child process to shut down.</param>
    /// <param name="signaler">Graceful signaler, or <see langword="null"/> for non-Run callers.</param>
    /// <param name="gracefulShutdownService">
    /// Owns the central graceful budget token, or <see langword="null"/> for non-Run callers.
    /// </param>
    /// <param name="gracefulBudgetActive">
    /// <see langword="false"/> when the central budget timer was never started (e.g. the dispose-only
    /// path), which forces the fallback so the ladder doesn't wait on a clock nobody started.
    /// </param>
    /// <param name="fallbackRequestGracefulShutdown">
    /// Whether the force-kill fallback should dispatch a best-effort graceful signal (SIGTERM) before
    /// killing. Typically <c>!OperatingSystem.IsWindows()</c>.
    /// </param>
    /// <param name="fallbackKillEntireProcessTree">Whether the force-kill fallback kills the whole tree.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="processDescription">Short human description used in log messages.</param>
    public static Task ShutdownAsync(
        Process process,
        IProcessTreeGracefulShutdownSignaler? signaler,
        GracefulShutdownService? gracefulShutdownService,
        bool gracefulBudgetActive,
        bool fallbackRequestGracefulShutdown,
        bool fallbackKillEntireProcessTree,
        ILogger logger,
        string processDescription)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processDescription);

        if (signaler is not null && gracefulShutdownService is not null && gracefulBudgetActive)
        {
            return ProcessGracefulShutdownLadder.ExecuteAsync(
                process,
                signaler,
                gracefulShutdownService.Token,
                logger,
                processDescription);
        }

        return ProcessTerminator.ShutdownAsync(
            process,
            requestGracefulShutdown: fallbackRequestGracefulShutdown,
            entireProcessTree: fallbackKillEntireProcessTree,
            logger,
            processDescription,
            gracefulShutdownCancellationToken: s_immediateEscalation);
    }
}
