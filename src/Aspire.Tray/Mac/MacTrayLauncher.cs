// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Shared;

namespace Aspire.Tray;

[SupportedOSPlatform("macos")]
internal static class MacTrayLauncher
{
    public static async Task StartAsync(TrayOptions options)
    {
        if (options.SmokeSeconds is not null || options.BundleRoot is null)
        {
            throw new ArgumentException("Starting the packaged tray requires --bundle-root and does not support smoke mode.");
        }

        // The helper shares the CLI's foreground process group. Token shielding in the CLI
        // cannot prevent Ctrl+C reaching us directly; finish the bounded lease handoff first.
        // Our own lease also protects the GUI while a terminating caller is unwinding.
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        using var lease = BundleVersionLease.Acquire(options.BundleRoot, "tray-launcher", "tray start");

        bool running;
        using (var probe = SingleInstance.TryAcquire())
        {
            running = probe is null;
        }
        if (running)
        {
            await TrayActivation.ShowExistingAsync(SingleInstance.ActivationPipeName, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var logPath = Path.Combine(SingleInstance.LegacyStateDirectoryPath, "aspire-tray.log");
        using var log = MacTrayLog.Open(logPath);
        // Spawn the actual GUI in a new session. Launch Services would reopen log paths
        // and hide the GUI process from us, preventing exact cleanup on a failed handoff.
        var process = MacDetachedProcess.Start(TrayLaunchCommand.CreateStartInfo(options), log);
        try
        {
            // The GUI acquires its own bundle lease before exposing this endpoint.
            await process.CompleteStartupAsync(
                () => TrayActivation.WaitUntilReadyAsync(SingleInstance.ActivationPipeName, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The tray did not become ready. See {logPath}. {ex.Message}", ex);
        }
    }

    public static async Task StopAsync()
    {
        using (var probe = SingleInstance.TryAcquire())
        {
            if (probe is not null)
            {
                return;
            }
        }

        TrayProcessIdentity identity;
        try
        {
            identity = await TrayActivation.StopExistingAsync(SingleInstance.ActivationPipeName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // Quit can release the lock between our initial probe and the IPC exchange.
            // Treat that as an idempotent stop only after verifying that no owner remains.
            using var probe = SingleInstance.TryAcquire();
            if (probe is null)
            {
                throw;
            }
            return;
        }
        using var timeout = new CancellationTokenSource(TrayActivation.RequestTimeout);
        try
        {
            await identity.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Never force-kill: a timed-out shutdown can still complete, and AppHosts are not ours.
            throw new TimeoutException("The tray accepted the stop request but has not exited.");
        }
    }
}
