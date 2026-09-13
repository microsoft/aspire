// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Shared;

namespace Aspire.Tray.Spike;

[SupportedOSPlatform("macos")]
internal static class MacTrayLauncher
{
    public static async Task StartAsync(SpikeOptions options)
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

        var logPath = Path.Combine(SingleInstance.DirectoryPath, "aspire-tray.log");
        using (var log = new FileStream(logPath, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
        }

        // Launch Services gives the GUI its own lifetime and log handles. Inheriting a CLI
        // stdout pipe would leave the long-lived app writing to a closed pipe after start exits.
        // -n ensures arguments reach our executable instead of activating another bundle version.
        var startInfo = TrayLaunchCommand.CreateStartInfo(options, logPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("macOS could not launch the tray.");
        using var timeout = new CancellationTokenSource(TrayActivation.RequestTimeout);
        var stdout = CliProcess.DrainAsync(process.StandardOutput, timeout.Token);
        var stderr = CliProcess.DrainAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            // The CLI retains its bundle lease until this succeeds. The GUI acquires its
            // own lease before exposing the endpoint; the reply also requires a live UI loop.
            // Even if open was interrupted, Launch Services may already have accepted the
            // launch. Wait for the acknowledgement before releasing either launcher lease.
            await TrayActivation.WaitUntilReadyAsync(SingleInstance.ActivationPipeName, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await CliProcess.TerminateOwnedChildAsync(process).ConfigureAwait(false);
            throw new TimeoutException($"The tray did not become ready (macOS launch exit {process.ExitCode}). See {logPath}.");
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
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
