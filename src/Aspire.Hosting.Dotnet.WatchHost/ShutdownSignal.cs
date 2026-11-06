// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Hosting.Dotnet.WatchHost;

/// <summary>
/// Turns the stop signals DCP or a terminal can send into cancellation instead of process termination.
/// </summary>
internal sealed class ShutdownSignal : IDisposable
{
    // On Windows these are Ctrl+C, Ctrl+Break, console close and shutdown/logoff.
    internal static PosixSignal[] Signals => [PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP];

    private readonly CancellationTokenSource _shutdown = new();
    private readonly PosixSignalRegistration[] _registrations;

    public ShutdownSignal()
    {
        _registrations = [.. Signals.Select(signal => PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            _shutdown.Cancel();
        }))];
    }

    public CancellationToken Token => _shutdown.Token;

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _shutdown.Dispose();
    }
}
