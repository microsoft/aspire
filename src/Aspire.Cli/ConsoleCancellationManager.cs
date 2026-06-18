// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli;

/// <summary>
/// Manages Ctrl+C, SIGINT, and SIGTERM signal handling with a shared <see cref="CancellationTokenSource"/>.
/// On the first termination signal it requests cooperative cancellation; after an optional graceful
/// window elapses it expires <see cref="GracefulShutdownService"/> so long-running ladders escalate to
/// forceful termination; after a final drain budget it signals
/// <see cref="ProcessTerminationCompletionSource"/> so <c>Program.Main</c> abandons the handler task and
/// returns the captured exit code.
/// </summary>
/// <remarks>
/// <para>
/// The three-stage signal counter mirrors the same ladder:
/// </para>
/// <list type="number">
///   <item>First signal — primary <see cref="Token"/> cancels and the graceful watcher starts.</item>
///   <item>Second signal — graceful window is collapsed via <see cref="GracefulShutdownService.Expire"/>;
///         ladders see the graceful token fire immediately and escalate.</item>
///   <item>Third (or later) signal — <see cref="ProcessTerminationCompletionSource"/> fires; Main exits NOW.</item>
/// </list>
/// <para>
/// Internal teardown paths (guest failures, normal completion) do NOT drive this counter. They rely on
/// disposable-driven cleanup — <c>await using</c> of the server session and guest launcher — to run each
/// child process's own per-process shutdown ladder when the run scope unwinds.
/// </para>
/// <para>
/// The completion source completing is treated as a strict superset of graceful expiration:
/// when the source completes for any reason (drain timeout, third signal, future external triggers),
/// <see cref="GracefulShutdownService.Expire"/> is invoked synchronously so ladders observing only
/// the graceful token unblock in time to issue a kill before Main abandons them.
/// </para>
/// <para>
/// Disposing this instance unregisters all signal handlers and disposes the internal token source.
/// The <see cref="GracefulShutdownService"/> is owned by the caller (typically <c>Program.Main</c>)
/// and is not disposed here.
/// </para>
/// </remarks>
internal sealed class ConsoleCancellationManager : IDisposable
{
    // Standard Unix exit codes: 128 + signal number (SIGINT=2, SIGTERM=15).
    // SigIntExitCode (130): used when the user presses Ctrl+C (SIGINT) or Ctrl+Break/SIGQUIT.
    // SigTermExitCode (143): used when the process receives SIGTERM (e.g. container stop, ProcessExit).
    private const int SigIntExitCode = 130;
    private const int SigTermExitCode = 143;

    private readonly CancellationTokenSource _cts = new();
    private readonly GracefulShutdownService _gracefulService;
    private readonly TimeSpan _finalDrainBudget;
    private readonly PosixSignalRegistration? _sigIntRegistration;
    private readonly PosixSignalRegistration? _sigTermRegistration;
    private readonly PosixSignalRegistration? _sigQuitRegistration;
    private readonly CancellationToken _token;
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

    public ConsoleCancellationManager(GracefulShutdownService gracefulService, TimeSpan finalDrainBudget)
    {
        ArgumentNullException.ThrowIfNull(gracefulService);

        _gracefulService = gracefulService;
        _finalDrainBudget = finalDrainBudget;
        _logger = NullLogger.Instance;

        // Set to a field so getting the token doesn't error after dispose.
        _token = _cts.Token;

        // Phase 3 → Phase 2 fallthrough. When the termination completion source completes for any reason
        // (drain timeout, third Ctrl+C, future external triggers), any ladder still observing only the
        // graceful token would otherwise sit on a Task.Delay(budget, gracefulService.Token) and miss its
        // last chance to issue a kill before Main abandons it. Cancel synchronously so this fires before
        // continuations of the completion source observe completion. Expire() is idempotent — multiple
        // calls across the watcher (Phase 1 end), the 2nd-signal branch, and this continuation are safe.
        _processTerminationCompletionSource.Task.ContinueWith(
            static (_, state) => ((GracefulShutdownService)state!).Expire(),
            _gracefulService,
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
    /// second termination signal, or process-termination completion). Convenience accessor — callers
    /// that already have a reference to <see cref="GracefulShutdownService"/> can read its
    /// <see cref="GracefulShutdownService.Token"/> directly.
    /// </summary>
    public CancellationToken GracefulShutdownToken => _gracefulService.Token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    /// <summary>
    /// Sets the graceful-shutdown budget for the currently-executing command by forwarding it to
    /// <see cref="GracefulShutdownService.Configure"/>. Default is zero, meaning ladders that consume
    /// <see cref="GracefulShutdownToken"/> fall through to escalation immediately (preserving today's
    /// behavior for every command that doesn't opt in). The <c>aspire run</c> handler calls this with
    /// five seconds so DCP and the AppHost get a real cooperative-shutdown window before escalation.
    /// </summary>
    public void ConfigureForCommand(TimeSpan gracefulBudget)
    {
        _gracefulService.Configure(gracefulBudget);
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
            _gracefulService.Expire();
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
            // Phase 1: graceful window. Start the central clock on the service, then wait for the
            // graceful token to fire. BeginGracefulWindow arms a CancelAfter(budget) (or, for a
            // zero-budget command, expires immediately), so the token is guaranteed to fire without us
            // owning a timer here. A 2nd Ctrl+C calls _gracefulService.Expire() from the signal counter,
            // which fires the token early and drops us straight into Phase 2.
            //
            // Under a debugger BeginGracefulWindow is a no-op (the developer needs unlimited time to
            // step), so the token never auto-fires and this await sits indefinitely — the right behavior
            // for stepping. A manual second Ctrl+C still escalates via Expire().
            _gracefulService.BeginGracefulWindow();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _gracefulService.Token).ConfigureAwait(false);
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
    }
}

