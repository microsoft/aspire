// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Shared;

namespace Aspire.TerminalHost;

/// <summary>
/// Runs a terminal host with process-level graceful shutdown handling.
/// </summary>
public static class TerminalHostProcessRunner
{
    /// <summary>
    /// Runs a terminal host until it exits, cancellation is requested, or the process receives
    /// SIGINT or SIGTERM.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var forceExitCts = new CancellationTokenSource();
        var parentWatchdog = ParentProcessWatchdog.Start(
            cts,
            KnownConfigNames.TerminalHostParentProcessId,
            KnownConfigNames.TerminalHostParentProcessStartedStable,
            legacyStartVariable: null);
        var terminationSignalReceived = 0;

        bool TryRequestGracefulShutdown()
        {
            if (Interlocked.Exchange(ref terminationSignalReceived, 1) != 0)
            {
                return false;
            }

            _ = ParentProcessWatchdog.CancelAndForceExitAsync(
                cts,
                forceExitCts.Token,
                ParentProcessWatchdog.ForceExitGracePeriod,
                Environment.Exit);
            return true;
        }

        void OnPosixSignal(PosixSignalContext context)
        {
            // The first signal grants TerminalHostApp a bounded window to unlink its sockets.
            // A second signal retains the platform default so operators can terminate immediately.
            context.Cancel = TryRequestGracefulShutdown();
        }

        PosixSignalRegistration? sigIntRegistration = null;
        PosixSignalRegistration? sigTermRegistration = null;
        PosixSignalRegistration? sigQuitRegistration = null;
        ConsoleCancelEventHandler? cancelKeyPressHandler = null;

        try
        {
            // PosixSignalRegistration also maps Windows console control events. Win32
            // TerminateProcess is not interceptable, so forced DCP termination on Windows can
            // bypass this cleanup; AppHost exact-path cleanup and the next startup sweep are the
            // backstops for that platform.
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
                    eventArgs.Cancel = TryRequestGracefulShutdown();
                };
                Console.CancelKeyPress += cancelKeyPressHandler;
            }

            return await TerminalHostApp.RunAsync(args, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            forceExitCts.Cancel();
            sigIntRegistration?.Dispose();
            sigTermRegistration?.Dispose();
            sigQuitRegistration?.Dispose();

            if (cancelKeyPressHandler is not null)
            {
                Console.CancelKeyPress -= cancelKeyPressHandler;
            }

            if (parentWatchdog is not null)
            {
                await parentWatchdog.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
