// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Shared;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray;

/// <summary>
/// Launches an independent Windows GUI and performs the bounded bundle lease handoff.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsTrayLauncher
{
    public static async Task StartAsync(TrayOptions options)
    {
        if (options.SmokeSeconds is not null || options.BundleRoot is null)
        {
            throw new ArgumentException("Starting the packaged tray requires --bundle-root and does not support smoke mode.");
        }

        using var lease = BundleVersionLease.Acquire(options.BundleRoot, "tray-launcher", "tray start");
        if (IsRunning())
        {
            await TrayActivation.ShowExistingAsync(WindowsSingleInstance.ActivationPipeName, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // Check log access before detaching. The GUI opens its own handle before exposing
        // activation, so neither the launcher's streams nor the CLI's pipes remain inherited.
        WindowsTrayLog.EnsureWritable();
        var startInfo = TrayLaunchCommand.CreateWindowsStartInfo(options);
        using var child = LaunchDetached(startInfo.FileName,
            TrayLaunchCommand.BuildWindowsCommandLine(startInfo), startInfo.WorkingDirectory);
        try
        {
            // The GUI acquires its lease before creating this endpoint. Readiness must be
            // acknowledged by a working native UI loop, not merely by a connected pipe.
            await CompleteStartupAsync(child,
                () => TrayActivation.WaitUntilReadyAsync(WindowsSingleInstance.ActivationPipeName, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The Windows tray did not acknowledge startup. See {WindowsTrayLog.LogPath}. {ex.Message}", ex);
        }
    }

    internal static async Task CompleteStartupAsync(SafeProcessHandle child, Func<Task> ready)
    {
        try
        {
            await ready().ConfigureAwait(false);
        }
        catch (Exception startupError)
        {
            try
            {
                TerminateAndWait(child);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Tray startup failed and the child could not be cleaned up.", startupError, cleanupError);
            }
            throw;
        }
    }

    public static async Task StopAsync()
    {
        if (!IsRunning())
        {
            return;
        }

        TrayProcessIdentity identity;
        try
        {
            identity = await TrayActivation.StopExistingAsync(WindowsSingleInstance.ActivationPipeName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // The owner can exit between the lock probe and IPC. Only report an idempotent
            // stop if a fresh probe proves there is no longer an owner.
            if (IsRunning())
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

    private static bool IsRunning()
    {
        // A named mutex is thread-affine. Never keep this probe across an await.
        using var probe = WindowsSingleInstance.TryAcquire();
        return probe is null;
    }

    private static unsafe SafeProcessHandle LaunchDetached(string executable, string commandLine, string workingDirectory)
    {
        const uint detachedProcess = 0x00000008;
        const uint breakawayFromJob = 0x01000000;
        const uint createSuspended = 0x00000004;
        var startup = new StartupInfo { Size = sizeof(StartupInfo) };
        var command = (commandLine + '\0').ToCharArray();
        fixed (char* commandPointer = command)
        {
            // Process.Start can inherit stdio and the caller's kill-on-close job. Do not
            // inherit any handles, console, or job lifetime; a restrictive job must fail
            // clearly rather than produce a GUI that disappears when the CLI exits.
            // BREAKAWAY is ignored when the parent has no job.
            // https://learn.microsoft.com/windows/win32/procthread/process-creation-flags
            if (!CreateProcess(executable, commandPointer, 0, 0, false,
                detachedProcess | breakawayFromJob | createSuspended, 0, workingDirectory, ref startup, out var process))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error,
                    $"Windows could not launch an independent tray process (native error {error}). A parent job may prohibit detached applications.");
            }
            using var thread = new SafeWaitHandle(process.Thread, ownsHandle: true);
            var child = new SafeProcessHandle(process.Process, ownsHandle: true);
            try
            {
                // Nested jobs can allow breaking away from only part of the hierarchy.
                // Verify independence before running any GUI code or starting its watcher.
                // https://learn.microsoft.com/windows/win32/procthread/nested-jobs
                if (!IsProcessInJob(child, 0, out var inJob))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not verify the tray's independent lifetime.");
                }
                if (inJob)
                {
                    throw new InvalidOperationException("A parent Windows job prevents the tray from running independently of the CLI.");
                }
                if (ResumeThread(thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not resume the tray process.");
                }

                return child;
            }
            catch
            {
                // Only this exact, still-suspended child is terminated. It has not started
                // discovery; never terminate a running tray or any AppHost during stop.
                using (child)
                {
                    TerminateAndWait(child);
                }
                throw;
            }
        }
    }

    private static void TerminateAndWait(SafeProcessHandle child)
    {
        // Keep the exact CreateProcess handle until exit is observed. A PID lookup or a
        // global stop request could target a different tray that won a concurrent start.
        var terminated = TerminateProcess(child, 1);
        var error = Marshal.GetLastPInvokeError();
        var wait = WaitForSingleObject(child, terminated ? 10000u : 0u);
        if (wait == 0)
        {
            return;
        }
        if (!terminated)
        {
            throw new Win32Exception(error, "Windows could not terminate the failed tray launch.");
        }
        if (wait == 258)
        {
            throw new TimeoutException("The failed tray launch did not exit after termination.");
        }
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not wait for the failed tray launch to exit.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(SafeProcessHandle process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(string applicationName, char* commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, nint environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public nint ReservedPointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
