// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.TerminalHost;

using var cts = new CancellationTokenSource();

// Cancelling the token flows into TerminalHostApp.RunAsync, which runs its cooperative
// teardown (TearDownAsync -> TerminalReplica.DisposeAsync) and unbinds + deletes this
// replica's producer/consumer UDS files ({id}.dcp.sock / {id}.host.sock) as its final
// action before the process exits.
void RequestCancellation()
{
    try
    {
        cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
        // A signal can race process teardown after `cts` is disposed (the app already
        // returned). There is nothing left to cancel, so ignore it.
    }
}

void OnPosixSignal(PosixSignalContext context)
{
    // Suppress the runtime's default action (immediate termination) so RunAsync gets to
    // drain gracefully instead of being torn down with the sockets still bound on disk.
    context.Cancel = true;
    RequestCancellation();
}

// Prefer PosixSignalRegistration over Console.CancelKeyPress: DCP stops a terminal-host
// resource by sending SIGTERM and waiting a short grace period (~10s) before it escalates to
// SIGKILL. Console.CancelKeyPress only observes Ctrl+C (SIGINT) and never sees SIGTERM, so a
// graceful `aspire stop` would otherwise skip teardown entirely and leak the producer/consumer
// sockets (https://github.com/microsoft/aspire/issues/19302). Despite the name,
// PosixSignalRegistration is supported on Windows: the runtime maps SIGINT -> CTRL_C_EVENT and
// SIGTERM -> CTRL_CLOSE_EVENT/CTRL_SHUTDOWN_EVENT. This mirrors src/Aspire.Cli/ConsoleCancellationManager.
PosixSignalRegistration? sigIntRegistration = null;
PosixSignalRegistration? sigTermRegistration = null;
PosixSignalRegistration? sigQuitRegistration = null;
try
{
    if (!OperatingSystem.IsBrowser()
        && !OperatingSystem.IsIOS()
        && !OperatingSystem.IsTvOS()
        && !OperatingSystem.IsAndroid())
    {
        sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
        sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);

        // SIGQUIT maps to CTRL_BREAK_EVENT on Windows; register it there for parity with the
        // previous Console.CancelKeyPress handler (which also fired on Ctrl+Break). On Unix,
        // leave SIGQUIT alone so its default core-dump stays available for debugging a hung host.
        if (OperatingSystem.IsWindows())
        {
            sigQuitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnPosixSignal);
        }
    }
    else
    {
        // Platforms without PosixSignalRegistration (browser/iOS/tvOS/Android). The terminal host
        // is a desktop-only process spawned by DCP, so this branch is defensive; it keeps at least
        // Ctrl+C working if the host is ever run on such a platform.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RequestCancellation();
        };
    }

    return await TerminalHostApp.RunAsync(args, cts.Token).ConfigureAwait(false);
}
finally
{
    sigIntRegistration?.Dispose();
    sigTermRegistration?.Dispose();
    sigQuitRegistration?.Dispose();
}
