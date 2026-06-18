// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli;

/// <summary>
/// The CLI's single shutdown service. Manages Ctrl+C, SIGINT, and SIGTERM signal handling with a
/// shared <see cref="CancellationTokenSource"/>, and owns the command-level graceful-shutdown policy:
/// the graceful budget (<see cref="IsEnabled"/>), the clock that bounds it, and the
/// <see cref="GracefulShutdownToken"/> the per-child shutdown ladders consume via
/// <see cref="IGracefulShutdownWindow"/>.
/// </summary>
/// <remarks>
/// <para>
/// On the first termination signal it requests cooperative cancellation; after the graceful window
/// elapses it expires <see cref="GracefulShutdownToken"/> so long-running ladders escalate to forceful
/// termination; after a final drain budget it signals <see cref="ProcessTerminationCompletionSource"/>
/// so <c>Program.Main</c> abandons the handler task and returns the captured exit code.
/// </para>
/// <para>
/// The three-stage signal counter mirrors the same ladder:
/// </para>
/// <list type="number">
///   <item>First signal — primary <see cref="Token"/> cancels and the graceful watcher starts.</item>
///   <item>Second signal — the graceful window is collapsed via <see cref="Expire"/>; ladders see
///         <see cref="GracefulShutdownToken"/> fire immediately and escalate.</item>
///   <item>Third (or later) signal — <see cref="ProcessTerminationCompletionSource"/> fires; Main exits NOW.</item>
/// </list>
/// <para>
/// Graceful shutdown is all-or-nothing per command: <see cref="IsEnabled"/> reflects whether a positive
/// budget was configured via <see cref="ConfigureForCommand"/>. <c>aspire run</c> configures a budget;
/// every other command leaves it at zero so its children force-kill immediately (preserving today's
/// behavior). The service self-bounds the window: <see cref="BeginGracefulWindow"/> arms a
/// <c>CancelAfter(budget)</c> so the token is guaranteed to fire once shutdown begins — regardless of
/// whether shutdown was initiated by a user signal or by disposal of a child owner. This is what lets
/// ladders consume the token without risking a hang.
/// </para>
/// <para>
/// Internal teardown paths (guest failures, normal completion) do NOT drive the signal counter. They rely
/// on disposable-driven cleanup — <c>await using</c> of the server session and guest launcher — to run each
/// child process's own per-process shutdown ladder when the run scope unwinds.
/// </para>
/// <para>
/// The completion source completing is treated as a strict superset of graceful expiration: when the source
/// completes for any reason (drain timeout, third signal, future external triggers), <see cref="Expire"/> is
/// invoked synchronously so ladders observing only the graceful token unblock in time to issue a kill before
/// Main abandons them.
/// </para>
/// <para>
/// Disposing this instance unregisters all signal handlers and disposes the internal token sources.
/// </para>
/// </remarks>
internal sealed class ConsoleCancellationManager : IDisposable, IGracefulShutdownWindow
{
    // Standard Unix exit codes: 128 + signal number (SIGINT=2, SIGTERM=15).
    // SigIntExitCode (130): used when the user presses Ctrl+C (SIGINT) or Ctrl+Break/SIGQUIT.
    // SigTermExitCode (143): used when the process receives SIGTERM (e.g. container stop, ProcessExit).
    private const int SigIntExitCode = 130;
    private const int SigTermExitCode = 143;

    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _gracefulCts = new();
    private readonly TimeSpan _finalDrainBudget;
    private readonly PosixSignalRegistration? _sigIntRegistration;
    private readonly PosixSignalRegistration? _sigTermRegistration;
    private readonly PosixSignalRegistration? _sigQuitRegistration;
    private readonly CancellationToken _token;
    private readonly CancellationToken _gracefulToken;
    // Graceful-shutdown budget for the running command. Zero (the default) means graceful shutdown is
    // disabled, so per-child ladders escalate to forceful termination immediately.
    private TimeSpan _gracefulBudget = TimeSpan.Zero;
    // Idempotency guard so the graceful clock (CancelAfter) is armed at most once.
    private int _gracefulWindowStarted;
    private ILogger _logger;
    private Task? _startedHandler;
    // Number of termination signals (Ctrl+C, SIGINT, SIGTERM, SIGQUIT, ProcessExit) received.
    // Drives the three-stage ladder: 1 = start graceful watcher; 2 = collapse graceful;
    // 3+ = force-exit. Internal teardown paths (guest failures, normal completion) do NOT
    // drive this counter — they rely on disposable-based cleanup (`await using` of the
    // server session + guest launcher) to run the per-process shutdown ladders.
    private int _signalCount;

    private readonly TaskCompletionSource<int> _processTerminationCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A completion source that is signaled with a native exit code when the running handler
    /// does not complete within the configured drain budget after a termination signal,
    /// or when a third Ctrl+C arrives.
    /// </summary>
    internal TaskCompletionSource<int> ProcessTerminationCompletionSource => _processTerminationCompletionSource;

    /// <summary>
    /// Sets the handler task that represents the currently executing command. When a termination
    /// signal arrives, the manager will wait for this task to complete within the configured budgets.
    /// </summary>
    internal void SetStartedHandler(Task handler) => Volatile.Write(ref _startedHandler, handler);

    /// <summary>
    /// Sets the logger instance used for diagnostic messages during signal handling.
    /// Call this once the logging infrastructure is available.
    /// </summary>
    internal void SetLogger(ILogger logger) => Volatile.Write(ref _logger, logger);

    public ConsoleCancellationManager(TimeSpan finalDrainBudget)
    {
        _finalDrainBudget = finalDrainBudget;
        _logger = NullLogger.Instance;

        // Capture tokens to fields so getting them doesn't error after dispose.
        _token = _cts.Token;
        _gracefulToken = _gracefulCts.Token;

        // Phase 3 → Phase 2 fallthrough. When the termination completion source completes for any reason
        // (drain timeout, third Ctrl+C, future external triggers), any ladder still observing only the
        // graceful token would otherwise sit on a Task.Delay(budget, GracefulShutdownToken) and miss its
        // last chance to issue a kill before Main abandons it. Cancel synchronously so this fires before
        // continuations of the completion source observe completion. Expire() is idempotent — multiple
        // calls across the watcher (Phase 1 end), the 2nd-signal branch, and this continuation are safe.
        _processTerminationCompletionSource.Task.ContinueWith(
            static (_, state) => ((ConsoleCancellationManager)state!).Expire(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Prefer PosixSignalRegistration for both SIGINT and SIGTERM as it handles
        // both signals uniformly and allows cancelling SIGTERM (which Console.CancelKeyPress cannot).
        // Despite the name, PosixSignalRegistration is supported on Windows: the runtime maps
        // SIGINT to CTRL_C_EVENT and SIGTERM to CTRL_CLOSE_EVENT/CTRL_SHUTDOWN_EVENT.
        if (!OperatingSystem.IsAndroid()
            && !OperatingSystem.IsIOS()
            && !OperatingSystem.IsTvOS()
            && !OperatingSystem.IsBrowser())
        {
            _sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
            _sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);

            // SIGQUIT maps to CTRL_BREAK_EVENT on Windows. Register it to maintain parity with
            // Console.CancelKeyPress which handled both Ctrl+C and Ctrl+Break.
            // On Linux/macOS, SIGQUIT's default action produces a core dump which is useful for
            // debugging hung processes — don't intercept it there.
            if (OperatingSystem.IsWindows())
            {
                _sigQuitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnPosixSignal);
            }
        }
        else
        {
            // Fall back to Console.CancelKeyPress on platforms that don't support PosixSignalRegistration.
            Console.CancelKeyPress += OnCancelKeyPress;
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public CancellationToken Token => _token;

    /// <summary>
    /// Token that fires when the graceful-shutdown window has been exhausted (graceful budget elapsed,
    /// second termination signal, or process-termination completion). Consumed by the per-child shutdown
    /// ladders through <see cref="IGracefulShutdownWindow"/>.
    /// </summary>
    public CancellationToken GracefulShutdownToken => _gracefulToken;

    /// <summary>
    /// Whether graceful shutdown is enabled for the running command — i.e. a positive budget was
    /// configured via <see cref="ConfigureForCommand"/>. When <see langword="false"/>, shutdown ladders
    /// escalate straight to forceful termination.
    /// </summary>
    public bool IsEnabled => _gracefulBudget > TimeSpan.Zero;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    /// <summary>
    /// Sets the graceful-shutdown budget for the currently-executing command. Default is zero, meaning
    /// ladders that consume <see cref="GracefulShutdownToken"/> fall through to escalation immediately
    /// (preserving today's behavior for every command that doesn't opt in). The <c>aspire run</c> handler
    /// calls this so DCP and the AppHost get a real cooperative-shutdown window before escalation.
    /// </summary>
    public void ConfigureForCommand(TimeSpan gracefulBudget)
    {
        if (gracefulBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracefulBudget), "Graceful budget cannot be negative.");
        }

        _gracefulBudget = gracefulBudget;
    }

    /// <summary>
    /// Starts the graceful-shutdown clock. Idempotent — the first caller arms a <c>CancelAfter(budget)</c>
    /// so <see cref="GracefulShutdownToken"/> is guaranteed to fire within the budget; subsequent calls are
    /// no-ops. Called by whoever initiates teardown (a user signal via <see cref="Cancel"/>, or a child
    /// owner's disposal-driven ladder) so the token is always bounded.
    /// </summary>
    public void BeginGracefulWindow()
    {
        // When a debugger is attached, never arm the clock — the developer needs unlimited time to step
        // through cancellation/cleanup logic. The token therefore never auto-fires; ladders that observe it
        // sit indefinitely (the right behavior for stepping). A manual second Ctrl+C still escalates because
        // it calls Expire() directly, bypassing this method.
        if (Debugger.IsAttached)
        {
            return;
        }

        // A non-positive budget means graceful shutdown isn't configured for this command; the window is
        // "over" the moment it begins, so escalate immediately.
        if (_gracefulBudget <= TimeSpan.Zero)
        {
            Expire();
            return;
        }

        if (Interlocked.Exchange(ref _gracefulWindowStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _gracefulCts.CancelAfter(_gracefulBudget);
        }
        catch (ObjectDisposedException)
        {
            // Racing process shutdown after dispose; the token's final state is already observable.
        }
    }

    /// <summary>
    /// Collapses the graceful-shutdown window immediately, regardless of the remaining budget. Safe to call
    /// multiple times from any thread; <see cref="GracefulShutdownToken"/> transitions to cancelled at most once.
    /// </summary>
    public void Expire()
    {
        try
        {
            _gracefulCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Expire can race with process shutdown after dispose; swallow rather than propagating so
            // callers (signal handlers, watcher continuations) never have to guard against it.
        }
    }

    private void OnPosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        var exitCode = context.Signal switch
        {
            PosixSignal.SIGINT => SigIntExitCode,
            PosixSignal.SIGQUIT => SigIntExitCode,
            _ => SigTermExitCode
        };
        Cancel(exitCode);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Cancel(SigIntExitCode);
    }

    private void OnProcessExit(object? sender, EventArgs e) => Cancel(SigTermExitCode);

    internal void Cancel(int exitCode)
    {
        var n = Interlocked.Increment(ref _signalCount);

        if (n == 1)
        {
            // First signal: request cooperative cancellation and schedule the graceful-then-drain
            // watcher. The signal handler returns immediately so Program.Main's Task.WhenAny observes
            // handler completion without being blocked by the handler thread.
            _logger.LogInformation("Termination signal received, requesting cancellation.");

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A signal can race with process shutdown after cancellation resources are disposed.
                return;
            }

            _ = ExpireGracefulThenFinalDrainAsync(exitCode);
        }
        else if (n == 2)
        {
            // Second signal: collapse Phase 1 immediately. Ladders observing the graceful token
            // unblock and escalate to forceful termination; the watcher's Task.Delay(graceful) gets
            // cancelled and moves on to Phase 2 (final drain).
            _logger.LogWarning("Second termination signal received, expiring graceful shutdown window.");
            Expire();
        }
        else
        {
            // Third (or later) signal: caller wants out NOW. Skip both graceful and drain budgets.
            _logger.LogWarning("Third termination signal received, forcing immediate exit.");
            _processTerminationCompletionSource.TrySetResult(exitCode);
        }
    }

    private async Task ExpireGracefulThenFinalDrainAsync(int forcedTerminationExitCode)
    {
        try
        {
            // Phase 1: graceful window. Start the central clock, then wait for the graceful token to
            // fire. BeginGracefulWindow arms a CancelAfter(budget) (or, for a zero-budget command,
            // expires immediately), so the token is guaranteed to fire without us owning a timer here.
            // A 2nd Ctrl+C calls Expire() from the signal counter, which fires the token early and drops
            // us straight into Phase 2.
            //
            // Under a debugger BeginGracefulWindow is a no-op (the developer needs unlimited time to
            // step), so the token never auto-fires and this await sits indefinitely — the right behavior
            // for stepping. A manual second Ctrl+C still escalates via Expire().
            BeginGracefulWindow();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _gracefulToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Graceful window expired (budget elapsed or 2nd Ctrl+C); fall through to Phase 2.
            }

            // Phase 2: final drain. Give the handler a chance to finish gracefully within the configured
            // drain budget. Task.WhenAny completes when either the handler or the delay finishes first,
            // without propagating exceptions from the losing task. It's ok that this delay isn't
            // cancellable — the process is ending.
            var startedHandler = Volatile.Read(ref _startedHandler);

            if (startedHandler is not null)
            {
                var drainTask = Task.Delay(_finalDrainBudget);

                if (await Task.WhenAny(startedHandler, drainTask).ConfigureAwait(false) == startedHandler)
                {
                    return;
                }
            }

            _logger.LogWarning("Handler did not complete within {Timeout}s after graceful expiration, forcing termination.", _finalDrainBudget.TotalSeconds);
            _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
        }
        catch (Exception)
        {
            // Any failure in the watcher path should still force termination rather than hang.
            _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
        }
    }

    public void Dispose()
    {
        _sigIntRegistration?.Dispose();
        _sigTermRegistration?.Dispose();
        _sigQuitRegistration?.Dispose();

        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        _cts.Dispose();
        _gracefulCts.Dispose();
    }
}

/// <summary>
/// The command-level graceful-shutdown window consumed by every per-child shutdown path
/// (<see cref="DotNet.ProcessExecution"/> and the ladders it drives). Implemented by
/// <see cref="ConsoleCancellationManager"/>, which owns the budget, the clock, and the token as part
/// of the single CLI shutdown service. This narrow contract is what the process-spawn sites depend on
/// so they don't take a dependency on the console signal manager in full. It lives in this file rather
/// than its own because it exists only to subdivide <see cref="ConsoleCancellationManager"/> when
/// referenced by the spawn sites.
/// </summary>
internal interface IGracefulShutdownWindow
{
    /// <summary>
    /// Whether graceful shutdown is enabled for the running command — i.e. a positive budget was
    /// configured. When <see langword="false"/>, shutdown ladders escalate straight to forceful
    /// termination.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Fires when the graceful-shutdown window has been exhausted (graceful budget elapsed, a second
    /// termination signal, or process-termination completion).
    /// </summary>
    CancellationToken GracefulShutdownToken { get; }

    /// <summary>
    /// Starts the graceful-shutdown clock. Idempotent — the first caller arms the budget so
    /// <see cref="GracefulShutdownToken"/> is guaranteed to fire within it; later calls are no-ops.
    /// Called by whoever initiates teardown (a user signal, or a child owner's disposal-driven ladder)
    /// so the token is always bounded.
    /// </summary>
    void BeginGracefulWindow();
}

