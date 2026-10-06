// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
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

    internal static unsafe SafeProcessHandle LaunchDetached(string executable, string commandLine, string workingDirectory)
    {
        const uint detachedProcess = 0x00000008;
        const uint breakawayFromJob = 0x01000000;
        const uint createSuspended = 0x00000004;
        const uint extendedStartupInfoPresent = 0x00080000;
        const nuint parentProcessAttribute = 0x00020000;
        using var shell = OpenDesktopShellProcess();
        nuint attributeSize = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref attributeSize);
        if (attributeSize == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not size the tray process attributes.");
        }
        var attributes = (nint)NativeMemory.Alloc(attributeSize);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not initialize the tray process attributes.");
            }
            initialized = true;
            // Inherit job membership from the same-user desktop shell, not the CLI or
            // terminal. BREAKAWAY alone can leave a child in an outer terminal job, while
            // IsProcessInJob also matches harmless system jobs that survive the CLI.
            // Keep CreateProcess (rather than ShellExecute) for the exact child handle,
            // invoking environment, and bounded readiness/cleanup protocol.
            // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
            var shellHandle = shell.DangerousGetHandle();
            if (!UpdateProcThreadAttribute(attributes, 0, parentProcessAttribute, &shellHandle, (nuint)sizeof(nint), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not select the desktop shell as the tray parent.");
            }
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = sizeof(StartupInfoEx) },
                Attributes = attributes
            };
            var command = (commandLine + '\0').ToCharArray();
            fixed (char* commandPointer = command)
            {
                if (!CreateProcess(executable, commandPointer, 0, 0, false,
                    detachedProcess | breakawayFromJob | createSuspended | extendedStartupInfoPresent,
                    0, workingDirectory, ref startup, out var process))
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(error,
                        $"Windows could not launch an independent tray process (native error {error}).");
                }
                using var thread = new SafeWaitHandle(process.Thread, ownsHandle: true);
                var child = new SafeProcessHandle(process.Process, ownsHandle: true);
                try
                {
                    if (ResumeThread(thread) == uint.MaxValue)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not resume the tray process.");
                    }

                    return child;
                }
                catch
                {
                    using (child)
                    {
                        TerminateAndWait(child);
                    }
                    throw;
                }
            }
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }
            NativeMemory.Free((void*)attributes);
        }
    }

    private static SafeProcessHandle OpenDesktopShellProcess()
    {
        var window = GetShellWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0)
        {
            throw new InvalidOperationException("Starting the Windows tray requires a running desktop shell.");
        }

        // Selecting a parent also selects its token. Do not switch users when the CLI
        // was launched with Run as different user on another user's desktop.
        const uint createProcess = 0x0080;
        const uint queryLimitedInformation = 0x1000;
        var shell = OpenProcess(createProcess | queryLimitedInformation, false, processId);
        try
        {
            if (shell.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not open the desktop shell for tray startup.");
            }
            if (!OpenProcessToken(shell, 0x0008, out var token))
            {
                var error = Marshal.GetLastPInvokeError();
                token.Dispose();
                throw new Win32Exception(error, "Windows could not verify the desktop shell's user.");
            }
            using (token)
            using (var shellIdentity = new WindowsIdentity(token.DangerousGetHandle()))
            using (var currentIdentity = WindowsIdentity.GetCurrent())
            {
                if (shellIdentity.User is null || currentIdentity.User is null ||
                    !shellIdentity.User.Equals(currentIdentity.User))
                {
                    throw new InvalidOperationException("The Windows tray must be started by the desktop shell's user.");
                }
            }

            return shell;
        }
        catch
        {
            shell.Dispose();
            throw;
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

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint attributes, uint count, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, void* value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributes);

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
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint Attributes;
    }

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
