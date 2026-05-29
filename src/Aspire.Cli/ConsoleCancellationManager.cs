// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli;

/// <summary>
/// Manages Ctrl+C, SIGINT, and SIGTERM signal handling with a shared CancellationTokenSource.
/// After cancellation is requested, schedules an asynchronous timeout for the running handler
/// to complete before signaling forced termination via <see cref="ProcessTerminationCompletionSource"/>.
/// A second signal forces immediate termination without waiting for the timeout.
/// Disposing this instance unregisters all signal handlers and disposes the token source.
/// </summary>
internal sealed class ConsoleCancellationManager : IDisposable
{
    // Standard Unix exit codes: 128 + signal number (SIGINT=2, SIGTERM=15).
    // SigIntExitCode (130): used when the user presses Ctrl+C (SIGINT) or Ctrl+Break/SIGQUIT.
    // SigTermExitCode (143): used when the process receives SIGTERM (e.g. container stop, ProcessExit).
    private const int SigIntExitCode = 130;
    private const int SigTermExitCode = 143;

    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _processTerminationTimeout;
    private readonly PosixSignalRegistration? _sigIntRegistration;
    private readonly PosixSignalRegistration? _sigTermRegistration;
    private readonly PosixSignalRegistration? _sigQuitRegistration;
    private readonly CancellationToken _token;
    private ILogger _logger;
    private Task<int>? _startedHandler;
    private int _cancelCalled;

    private readonly TaskCompletionSource<int> _processTerminationCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A completion source that is signaled with a native exit code when the running handler
    /// does not complete within the configured timeout after a termination signal.
    /// </summary>
    internal TaskCompletionSource<int> ProcessTerminationCompletionSource => _processTerminationCompletionSource;

    /// <summary>
    /// Sets the handler task that represents the currently executing command. When a termination
    /// signal arrives, the manager will wait for this task to complete within the configured timeout.
    /// </summary>
    internal void SetStartedHandler(Task<int> handler) => Volatile.Write(ref _startedHandler, handler);

    /// <summary>
    /// Sets the logger instance used for diagnostic messages during signal handling.
    /// Call this once the logging infrastructure is available.
    /// </summary>
    internal void SetLogger(ILogger logger) => Volatile.Write(ref _logger, logger);

    public ConsoleCancellationManager(TimeSpan processTerminationTimeout)
    {
        _processTerminationTimeout = processTerminationTimeout;
        _logger = NullLogger.Instance;

        // Set to a field so getting the token doesn't error after dispose.
        _token = _cts.Token;

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

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

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

    internal void Cancel(int forcedTerminationExitCode)
    {
        var signalCount = Interlocked.Increment(ref _cancelCalled);

        if (signalCount == 1)
        {
            // First signal: request cooperative cancellation and schedule an async timeout
            // that will force-terminate if the handler doesn't complete in time.
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

            // Schedule the forced-completion timeout asynchronously so the signal handler
            // returns immediately. This allows Program.Main's Task.WhenAny to observe
            // handlerTask completion without being blocked by the signal handler thread.
            _ = ForceTerminationAfterTimeoutAsync(forcedTerminationExitCode);
        }
        else
        {
            // Second (or subsequent) signal: force immediate termination without waiting.
            _logger.LogWarning("Second termination signal received, forcing immediate exit.");
            _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
        }
    }

    private async Task ForceTerminationAfterTimeoutAsync(int forcedTerminationExitCode)
    {
        try
        {
            // When a debugger is attached, don't force-terminate — the developer needs
            // unlimited time to step through cancellation/cleanup logic.
            if (Debugger.IsAttached)
            {
                return;
            }

            var startedHandler = Volatile.Read(ref _startedHandler);

            if (startedHandler is not null)
            {
                // Give the handler a chance to finish gracefully within the configured timeout.
                // Task.WhenAny completes when either the handler or the delay finishes first,
                // without propagating exceptions from the losing task.
                // It's ok that this delay isn't cancellable. The process is ending.
                var timeoutTask = Task.Delay(_processTerminationTimeout);
                if (await Task.WhenAny(startedHandler, timeoutTask).ConfigureAwait(false) == startedHandler)
                {
                    // Handler finished within the timeout; no forced termination needed.
                    return;
                }
            }

            _logger.LogWarning("Handler did not complete within {Timeout}s, forcing termination.", _processTerminationTimeout.TotalSeconds);
            _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
        }
        catch (Exception)
        {
            // Any failure in the timeout path should still force termination rather than hang.
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
