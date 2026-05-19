// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Cli;

/// <summary>
/// Manages Ctrl+C, SIGINT, and SIGTERM signal handling with a shared CancellationTokenSource.
/// After cancellation is requested, waits up to <c>processTerminationTimeout</c> for the running
/// handler to complete before signaling forced termination via <see cref="ProcessTerminationCompletionSource"/>.
/// Disposing this instance unregisters all signal handlers and disposes the token source.
/// </summary>
internal sealed class ConsoleCancellationManager : IDisposable
{
    private const int SigIntExitCode = 130;
    private const int SigTermExitCode = 143;

    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _processTerminationTimeout;
    private readonly PosixSignalRegistration? _sigIntRegistration;
    private readonly PosixSignalRegistration? _sigTermRegistration;
    private readonly CancellationToken _token;
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

    public ConsoleCancellationManager(TimeSpan processTerminationTimeout)
    {
        _processTerminationTimeout = processTerminationTimeout;

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
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public CancellationToken Token => _token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    private void OnPosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Cancel(context.Signal == PosixSignal.SIGINT ? SigIntExitCode : SigTermExitCode);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Cancel(SigIntExitCode);
    }

    private void OnProcessExit(object? sender, EventArgs e) => Cancel(SigTermExitCode);

    private void Cancel(int forcedTerminationExitCode)
    {
        // Ensure only the first signal triggers cancellation logic; subsequent signals are no-ops.
        if (Interlocked.CompareExchange(ref _cancelCalled, 1, 0) != 0)
        {
            return;
        }

        // Request cancellation so cooperative listeners can begin shutting down.
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A signal can race with process shutdown after cancellation resources are disposed.
            return;
        }

        try
        {
            var startedHandler = Volatile.Read(ref _startedHandler);

            // Wait for the configured interval to allow graceful shutdown.
            if (startedHandler is null || !startedHandler.Wait(_processTerminationTimeout))
            {
                // If the handler does not finish within configured time, use the completion
                // source to signal forced completion (preserving native exit code).
                _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
            }
        }
        catch (AggregateException)
        {
            // The task was cancelled or an exception was thrown during task execution.
        }
    }

    public void Dispose()
    {
        _sigIntRegistration?.Dispose();
        _sigTermRegistration?.Dispose();

        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        _cts.Dispose();
    }
}
