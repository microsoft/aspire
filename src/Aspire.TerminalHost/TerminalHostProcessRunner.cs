// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.TerminalHost;

/// <summary>
/// Runs a terminal host with process-level graceful shutdown handling.
/// </summary>
internal static class TerminalHostProcessRunner
{
    /// <summary>
    /// Runs a terminal host until it exits or the process receives SIGINT or SIGTERM.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();

        void RequestCancellation()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A signal can race process teardown after cancellation has been disposed.
            }
        }

        void OnPosixSignal(PosixSignalContext context)
        {
            // Suppress immediate process termination so TerminalHostApp can unlink its sockets.
            context.Cancel = true;
            RequestCancellation();
        }

        PosixSignalRegistration? sigIntRegistration = null;
        PosixSignalRegistration? sigTermRegistration = null;
        PosixSignalRegistration? sigQuitRegistration = null;
        ConsoleCancelEventHandler? cancelKeyPressHandler = null;

        try
        {
            // PosixSignalRegistration also maps Windows console control events. Sharing this
            // runner keeps the standalone executable and bundled aspire-managed path aligned.
            if (!OperatingSystem.IsBrowser()
                && !OperatingSystem.IsIOS()
                && !OperatingSystem.IsTvOS()
                && !OperatingSystem.IsAndroid())
            {
                sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
                sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);

                // SIGQUIT maps to Ctrl+Break on Windows. Preserve the previous
                // Console.CancelKeyPress behavior without intercepting Unix core dumps.
                if (OperatingSystem.IsWindows())
                {
                    sigQuitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnPosixSignal);
                }
            }
            else
            {
                // Terminal hosts are desktop processes, but retain Ctrl+C behavior if this
                // executable is ever used on a platform without PosixSignalRegistration.
                cancelKeyPressHandler = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    RequestCancellation();
                };
                Console.CancelKeyPress += cancelKeyPressHandler;
            }

            return await TerminalHostApp.RunAsync(args, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            sigIntRegistration?.Dispose();
            sigTermRegistration?.Dispose();
            sigQuitRegistration?.Dispose();

            if (cancelKeyPressHandler is not null)
            {
                Console.CancelKeyPress -= cancelKeyPressHandler;
            }
        }
    }
}
