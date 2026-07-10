// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Processes;

internal interface IProcessHandleTracker : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessHandleTracker(SafeProcessHandle processHandle, string ownerName) : IProcessHandleTracker
{
    public bool HasExited
    {
        get
        {
            ThrowIfDisposed(nameof(HasExited));

            var waitResult = WindowsProcessInterop.WaitForSingleObject(processHandle, 0);
            return waitResult switch
            {
                WindowsProcessInterop.WaitObject0 => true,
                WindowsProcessInterop.WaitTimeout => false,
                WindowsProcessInterop.WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForSingleObject failed while reading {ownerName}.HasExited"),
                _ => throw new InvalidOperationException($"Unexpected WaitForSingleObject result: 0x{waitResult:X8}"),
            };
        }
    }

    public int ExitCode
    {
        get
        {
            ThrowIfDisposed(nameof(ExitCode));

            // Disambiguate STILL_ACTIVE (259) from a real 259 exit code via a zero-timeout wait.
            var waitResult = WindowsProcessInterop.WaitForSingleObject(processHandle, 0);
            if (waitResult == WindowsProcessInterop.WaitTimeout)
            {
                throw new InvalidOperationException("Process has not exited; cannot read ExitCode.");
            }
            if (waitResult == WindowsProcessInterop.WaitFailed)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForSingleObject failed while reading {ownerName}.ExitCode");
            }

            if (!WindowsProcessInterop.GetExitCodeProcess(processHandle, out var exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetExitCodeProcess failed while reading {ownerName}.ExitCode");
            }

            return unchecked((int)exitCode);
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed(nameof(WaitForExitAsync));
        return WindowsProcessInterop.WaitForExitAsync(processHandle, cancellationToken);
    }

    public void Dispose()
    {
        processHandle.Dispose();
    }

    private void ThrowIfDisposed(string memberName)
    {
        if (processHandle.IsClosed || processHandle.IsInvalid)
        {
            throw new InvalidOperationException($"Cannot read {memberName} after the {ownerName} process handle has been disposed.");
        }
    }
}
