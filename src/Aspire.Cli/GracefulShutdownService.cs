// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli;

/// <summary>
/// The single authority for the CLI's command-level graceful-shutdown policy. Owns the
/// graceful budget (the time limit configured for the running command) and the
/// <see cref="CancellationTokenSource"/> that fires when that window expires. Consumed by every
/// long-running shutdown ladder (e.g. <c>AppHostServerSession</c>, <c>ProcessGuestLauncher</c>,
/// <c>ProcessExecution</c>) so the decision to wait for a cooperative shutdown vs.
/// escalate to forceful termination is made once, for the whole command, rather than per child or
/// per process execution.
/// </summary>
/// <remarks>
/// <para>
/// Graceful shutdown is all-or-nothing per command: <see cref="IsEnabled"/> reflects whether a
/// positive time limit was configured via <see cref="Configure"/>. <c>aspire run</c> configures a
/// budget; every other command leaves it at zero so its children force-kill immediately
/// (preserving today's behavior).
/// </para>
/// <para>
/// The service also owns the timer. <see cref="BeginGracefulWindow"/> idempotently arms a
/// <c>CancelAfter(budget)</c> so the token is guaranteed to fire within the budget once shutdown
/// begins — regardless of whether shutdown was initiated by a user signal (<see cref="ConsoleCancellationManager"/>)
/// or by disposal of a child owner. This is what lets ladders consume the token without risking a
/// hang: there is no path where a ladder waits on a clock that nobody started.
/// </para>
/// <para>
/// Multiple <see cref="Expire"/> / <see cref="BeginGracefulWindow"/> calls are safe and idempotent;
/// the token transitions from un-cancelled to cancelled exactly once.
/// </para>
/// <para>
/// Registered as a DI singleton via <c>services.AddSingleton(instance)</c> so the container does not
/// take disposal ownership; the bootstrap path (<c>Program.Main</c>) owns the instance lifetime
/// alongside CCM.
/// </para>
/// </remarks>
internal sealed class GracefulShutdownService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private TimeSpan _budget = TimeSpan.Zero;
    private int _windowStarted;

    public GracefulShutdownService()
    {
        // Capture the token up front so callers can still observe its (final) state
        // after Dispose, matching the pattern used by ConsoleCancellationManager.
        _token = _cts.Token;
    }

    /// <summary>
    /// Fires when the CLI's graceful-shutdown budget has been exhausted (or when an
    /// external signal has determined that further waiting is no longer useful).
    /// </summary>
    public CancellationToken Token => _token;

    /// <summary>
    /// Whether graceful shutdown is enabled for the running command — i.e. a positive budget was
    /// configured via <see cref="Configure"/>. When <see langword="false"/>, shutdown ladders
    /// escalate straight to forceful termination.
    /// </summary>
    public bool IsEnabled => _budget > TimeSpan.Zero;

    /// <summary>
    /// Sets the graceful-shutdown budget for the running command. Default is zero
    /// (<see cref="IsEnabled"/> is <see langword="false"/>). The <c>aspire run</c> handler configures
    /// a positive budget so DCP and the AppHost get a real cooperative-shutdown window before
    /// escalation.
    /// </summary>
    public void Configure(TimeSpan budget)
    {
        if (budget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Graceful budget cannot be negative.");
        }

        _budget = budget;
    }

    /// <summary>
    /// Starts the graceful-shutdown clock. Idempotent — the first caller arms a
    /// <c>CancelAfter(budget)</c> so <see cref="Token"/> is guaranteed to fire within the budget;
    /// subsequent calls are no-ops. Called by whoever initiates teardown (a user signal via CCM, or a
    /// child owner's disposal-driven ladder) so the token is always bounded.
    /// </summary>
    public void BeginGracefulWindow()
    {
        // When a debugger is attached, never arm the clock — the developer needs unlimited time to
        // step through cancellation/cleanup logic. The token therefore never auto-fires; ladders that
        // observe it sit indefinitely (the right behavior for stepping). A manual second Ctrl+C still
        // escalates because it calls Expire() directly, bypassing this method.
        if (Debugger.IsAttached)
        {
            return;
        }

        // A non-positive budget means graceful shutdown isn't configured for this command; the window
        // is "over" the moment it begins, so escalate immediately.
        if (_budget <= TimeSpan.Zero)
        {
            Expire();
            return;
        }

        if (Interlocked.Exchange(ref _windowStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.CancelAfter(_budget);
        }
        catch (ObjectDisposedException)
        {
            // Racing process shutdown after dispose; the token's final state is already observable.
        }
    }

    /// <summary>
    /// Signals that the graceful-shutdown window is over immediately, regardless of the remaining
    /// budget. Safe to call multiple times from any thread; the underlying token transitions to
    /// cancelled at most once.
    /// </summary>
    public void Expire()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Expire can race with process shutdown after dispose; swallow rather
            // than propagating so callers (signal handlers, watcher continuations)
            // never have to guard against it.
        }
    }

    public void Dispose() => _cts.Dispose();
}
