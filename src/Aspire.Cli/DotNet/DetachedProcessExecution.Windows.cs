// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.Processes;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.DotNet;

internal sealed partial class DetachedProcessExecution
{
    /// <summary>
    /// Windows implementation using <see cref="WindowsProcessInterop.SpawnProcess"/>
    /// with NUL bound to stdout and stderr (stdin is left unset). The detached child is NOT
    /// assigned to the CLI's kill-on-parent-exit job — the entire point of <c>aspire start</c> is
    /// that the AppHost outlives the launching CLI.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static DetachedChildProcess StartWindows(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
    {
        // Open NUL for the child's stdout/stderr — child writes go nowhere. The handle must be
        // inheritable (PROC_THREAD_ATTRIBUTE_HANDLE_LIST whitelists but does NOT promote
        // non-inheritable handles).
        using var nulHandle = WindowsProcessInterop.CreateFileW(
            "NUL",
            WindowsProcessInterop.GenericWrite,
            WindowsProcessInterop.FileShareWrite,
            nint.Zero,
            WindowsProcessInterop.OpenExisting,
            0,
            nint.Zero);

        if (nulHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open NUL device");
        }

        if (!WindowsProcessInterop.SetHandleInformation(nulHandle, WindowsProcessInterop.HandleFlagInherit, WindowsProcessInterop.HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set NUL handle inheritance");
        }

        var nulRawHandle = nulHandle.DangerousGetHandle();
        var stdio = new WindowsProcessInterop.StdioHandles(
            Stdin: nint.Zero,
            Stdout: nulRawHandle,
            Stderr: nulRawHandle);

        // jobHandle: null — detached children must survive a CLI crash. Anything assigned to
        // the CLI's kill-on-parent-exit job dies with the CLI, which is the opposite of what
        // `aspire start` wants.
        var pi = WindowsProcessInterop.SpawnProcess(
            fileName,
            arguments,
            workingDirectory,
            stdio,
            environment,
            createNewConsole: true,
            jobHandle: null);

        WindowsProcessHandleTracker? processHandle = new(new SafeProcessHandle(pi.hProcess, ownsHandle: true), nameof(DetachedProcessExecution));
        try
        {
            var detachedProcess = System.Diagnostics.Process.GetProcessById(pi.dwProcessId);
            WindowsProcessInterop.CloseHandle(pi.hThread);

            var capturedProcessHandle = processHandle;
            processHandle = null;

            return new DetachedChildProcess(detachedProcess, exitMonitorProcess: null, capturedProcessHandle);
        }
        catch
        {
            try { WindowsProcessInterop.TerminateProcess(pi.hProcess, 1); } catch { }
            try { WindowsProcessInterop.CloseHandle(pi.hThread); } catch { }
            processHandle?.Dispose();
            throw;
        }
    }
}
