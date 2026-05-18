// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Cli;

/// <summary>
/// Manages Ctrl+C and SIGTERM signal handling with a shared CancellationTokenSource.
/// Disposing this instance unregisters all signal handlers and disposes the token source.
/// </summary>
internal sealed class ConsoleCancellationManager : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly ConsoleCancelEventHandler _cancelKeyPressHandler;
    private readonly PosixSignalRegistration? _sigTermRegistration;

    public ConsoleCancellationManager()
    {
        _token = _cts.Token;
        _cancelKeyPressHandler = (sender, eventArgs) =>
        {
            TryCancel();
            eventArgs.Cancel = true;
        };
        Console.CancelKeyPress += _cancelKeyPressHandler;

        _sigTermRegistration = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                TryCancel();
                context.Cancel = true;
            });
    }

    public CancellationToken Token => _token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    private void TryCancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A signal can race with process shutdown after cancellation resources are disposed.
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= _cancelKeyPressHandler;
        _sigTermRegistration?.Dispose();
        _cts.Dispose();
    }
}
