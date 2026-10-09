// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Shared;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray;

/// <summary>
/// Owns the per-user Linux singleton and the detached, acknowledged bundle handoff.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxTrayRuntime
{
    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "tray", "runtime");
    internal static string PipeName => Path.Combine(DirectoryPath, "control-v1.sock");
    internal static string LogPath => Path.Combine(DirectoryPath, "aspire-tray.log");

    internal static FileStream? TryAcquire(string directory)
    {
        PrepareDirectory(directory);
        var handle = OpenPrivateFile(Path.Combine(directory, "instance.lock"), append: false);
        if (flock(handle, 2 | 4) == 0)
        {
            // Never unlink the lock: contenders must continue to lock the same inode.
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error == 11) // Linux EWOULDBLOCK.
        {
            return null;
        }
        throw new Win32Exception(error, "Could not acquire the tray instance lock.");
    }

    private static void PrepareDirectory(string directory)
    {
        for (var entry = new DirectoryInfo(directory); entry is not null; entry = entry.Parent)
        {
            if (entry.LinkTarget is not null)
            {
                throw new IOException("The tray runtime directory must not contain symbolic links.");
            }
        }
        DirectoryHelper.CreateWithOwnerOnlyPermissions(directory);
    }

    private static SafeFileHandle OpenPrivateFile(string path, bool append)
    {
        // Linux O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC; reject substituted links.
        var descriptor = open(path, 2 | 0x40 | 0x20000 | 0x80000 | (append ? 0x400 : 0), 0x180);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the tray runtime file.");
        }
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        if (fchmod(handle, 0x180) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Could not restrict tray runtime file permissions.");
        }
        return handle;
    }

    internal static void Detach()
    {
        // This runs in the newly launched executable, not by forking a managed runtime.
        // The child leaves the terminal session before initializing GTK or discovery.
        if (setsid() < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not detach the tray from the terminal.");
        }
        PrepareDirectory(DirectoryPath);
        using var originalLog = OpenPrivateFile(LogPath, append: true);
        using var originalInput = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read);
        using var log = DuplicateAboveStdio(originalLog);
        using var input = DuplicateAboveStdio(originalInput);
        // Close possible descriptors 0/1/2 before replacing them. Keeping independent
        // descriptors above stdio also prevents one dup2 from clobbering another source.
        originalLog.Dispose();
        originalInput.Dispose();
        if (dup2(log, 1) < 0 || dup2(log, 2) < 0 || dup2(input, 0) < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not redirect detached tray streams.");
        }
    }

    private static SafeFileHandle DuplicateAboveStdio(SafeFileHandle handle)
    {
        var descriptor = fcntl(handle, 1030, 3); // Linux F_DUPFD_CLOEXEC.
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not duplicate a tray stream.");
        }
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    internal static ProcessStartInfo CreateStartInfo(TrayOptions options)
    {
        if (options.BundleRoot is null || options.SmokeSeconds is not null)
        {
            throw new ArgumentException("Packaged start requires --bundle-root and does not support smoke mode.");
        }
        var executable = Path.Combine(options.BundleRoot, "tray", "aspire-tray");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The Linux tray payload is missing.", executable);
        }
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in new[] { "--detached", "--cli", options.CliPath, "--bundle-root", options.BundleRoot })
        {
            info.ArgumentList.Add(argument);
        }
        if (options.StartupCliPath is { } startup)
        {
            info.ArgumentList.Add("--startup-cli");
            info.ArgumentList.Add(startup);
        }
        return info;
    }

    internal static async Task StartAsync(TrayOptions options)
    {
        var startInfo = CreateStartInfo(options);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        using var lease = BundleVersionLease.Acquire(options.BundleRoot!, "tray-launcher", "tray start");
        bool running;
        using (var probe = TryAcquire(DirectoryPath))
        {
            running = probe is null;
        }
        if (running)
        {
            await TrayActivation.ShowExistingAsync(PipeName, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch the tray.");
        using var cancellation = new CancellationTokenSource();
        var ready = TrayActivation.WaitUntilReadyAsync(PipeName, cancellation.Token);
        try
        {
            var exited = child.WaitForExitAsync(cancellation.Token);
            if (await Task.WhenAny(ready, exited).ConfigureAwait(false) == exited && child.ExitCode != 0)
            {
                throw new InvalidOperationException($"The tray exited during startup. See {LogPath}.");
            }
            await ready.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!child.HasExited)
            {
                try
                {
                    // A rejected readiness reply can reach us before Program finishes
                    // disposing discovery and its lease. Let that normal cleanup finish.
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Only this newly launched child is ours, never an arbitrary tree
                    // that might already include a user-started AppHost.
                    child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException($"The Linux tray did not become ready. A graphical session, GTK 3, AppIndicator library and StatusNotifierWatcher are required. On GNOME enable an AppIndicator extension; on Waybar enable the tray module. See {LogPath}.", ex);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await ready.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException)
            {
                // Observe a pending readiness task after the launch failure was reported.
            }
        }
    }

    internal static async Task StopAsync()
    {
        using (var probe = TryAcquire(DirectoryPath))
        {
            if (probe is not null)
            {
                return;
            }
        }
        TrayProcessIdentity identity;
        try
        {
            identity = await TrayActivation.StopExistingAsync(PipeName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            using var probe = TryAcquire(DirectoryPath);
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
            throw new TimeoutException("The tray accepted the stop request but has not exited.");
        }
    }

    [LibraryImport("libc", SetLastError = true)] private static partial int setsid();
    [LibraryImport("libc", SetLastError = true)] private static partial int flock(SafeFileHandle handle, int operation);
    [LibraryImport("libc", SetLastError = true)] private static partial int dup2(SafeFileHandle handle, int destination);
    [LibraryImport("libc", SetLastError = true)] private static partial int fchmod(SafeFileHandle handle, uint mode);
    [LibraryImport("libc", SetLastError = true)] private static partial int fcntl(SafeFileHandle handle, int command, int argument);
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int open(string path, int flags, uint mode);
}
