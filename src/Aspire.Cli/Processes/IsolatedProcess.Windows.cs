// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Processes;

internal sealed partial class IsolatedProcess
{
    /// <summary>
    /// Windows implementation. Opens NUL for stdin and anonymous pipes for stdout/stderr,
    /// then delegates to <see cref="WindowsProcessInterop.SpawnProcess"/> for the actual
    /// <c>CreateProcessW</c> ceremony. Console isolation and parent-exit protection are
    /// independent: when <see cref="IsolatedProcessStartInfo.KillOnParentExit"/> is set, the spawn
    /// primitive assigns the child to the kill-on-close job atomically at creation
    /// (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>) even if the child does not need a new console group.
    /// </summary>
    /// <remarks>
    /// When the child is not detached, this launcher consumes stdout/stderr line-by-line via
    /// anonymous pipes. stdin is wired to NUL because we don't supply input to the interactive
    /// child, but Windows still requires a valid handle when STARTF_USESTDHANDLES is set and
    /// the other two stdio handles are real pipes — passing IntPtr.Zero in that combination
    /// leaves child stdin referencing whatever default the loader picks, which has tripped up
    /// some test runners in the past.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static StartedProcess StartWindows(IsolatedProcessStartInfo startInfo)
    {
        if (startInfo.Detached)
        {
            return StartWindowsSuppressed(startInfo);
        }

        var nulStdinHandle = WindowsProcessInterop.CreateFileW(
            "NUL",
            WindowsProcessInterop.GenericRead,
            WindowsProcessInterop.FileShareRead,
            nint.Zero,
            WindowsProcessInterop.OpenExisting,
            0,
            nint.Zero);

        if (nulStdinHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open NUL device for stdin");
        }

        AnonymousPipeServerStream? stdoutPipe = null;
        AnonymousPipeServerStream? stderrPipe = null;

        try
        {
            if (!WindowsProcessInterop.SetHandleInformation(nulStdinHandle, WindowsProcessInterop.HandleFlagInherit, WindowsProcessInterop.HandleFlagInherit))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set NUL stdin handle inheritance");
            }

            // PipeDirection.In = server reads, client writes. Inheritable is REQUIRED:
            // PROC_THREAD_ATTRIBUTE_HANDLE_LIST restricts WHICH handles get inherited but does
            // NOT promote non-inheritable handles to inheritable ones. Without this flag the
            // child would see ERROR_INVALID_HANDLE on its stdout/stderr writes.
            stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

            var stdio = new WindowsProcessInterop.StdioHandles(
                Stdin: nulStdinHandle.DangerousGetHandle(),
                Stdout: stdoutPipe.ClientSafePipeHandle.DangerousGetHandle(),
                Stderr: stderrPipe.ClientSafePipeHandle.DangerousGetHandle());

            // Pass the caller's environment (if touched) verbatim to the spawn primitive;
            // null = inherit parent env block.
            var environment = startInfo.GetEnvironmentForSpawn();

            // Parent-exit protection is independent from console isolation. A child can need a fresh
            // console group for targeted CTRL+C without also needing the kill-on-close job, and detached
            // children intentionally omit the job so they survive the launching CLI.
            var jobHandle = startInfo.KillOnParentExit ? WindowsConsoleProcessJob.Shared.Handle : null;
            var pi = WindowsProcessInterop.SpawnProcess(
                startInfo.FileName,
                startInfo.ArgumentList,
                startInfo.WorkingDirectory,
                stdio,
                environment,
                createNewConsole: startInfo.IsolateConsole,
                jobHandle);

            // CreateProcess succeeded; from here, any failure must terminate the just-created
            // child instead of letting it run orphaned. Drop the parent-side copy of the
            // client write ends so EOF reaches the StreamReader pumps when the child closes
            // its handle on exit — without this, the pump would never see EOF and disposal
            // would always have to wait for the drain timeout.

            // Take ownership of pi.hProcess in a SafeProcessHandle FIRST so that any failure
            // below (including OOM in the inner try) cannot leak the raw handle; the
            // SafeProcessHandle finalizer will close it. We keep this handle for the lifetime
            // of the IsolatedProcess so that ExitCode / HasExited can query the child via
            // GetExitCodeProcess / WaitForSingleObject directly. Process objects obtained via
            // Process.GetProcessById cannot reliably surface ExitCode on Windows — see
            // https://github.com/dotnet/runtime/issues/45003. Holding the original
            // CreateProcess handle also pins the OS process object so a recycled PID cannot
            // redirect GetProcessById to a different process during the brief window between
            // CreateProcess returning and GetProcessById running.
            SafeProcessHandle? processHandle = new(pi.hProcess, ownsHandle: true);
            try
            {
                stdoutPipe.DisposeLocalCopyOfClientHandle();
                stderrPipe.DisposeLocalCopyOfClientHandle();

                // Stdin NUL is no longer needed in the parent — only the child needs it.
                // Releasing here avoids a fd leak per spawned child.
                nulStdinHandle.Dispose();

                var process = Process.GetProcessById(pi.dwProcessId);

                // pi.hThread is no longer needed; the SafeProcessHandle owns pi.hProcess.
                WindowsProcessInterop.CloseHandle(pi.hThread);
                pi.hThread = nint.Zero;

                // UTF-8 with non-throwing fallback — a stray OEM-encoded byte from a tsx
                // warning shouldn't kill the pump. Mojibake is the documented tradeoff.
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                var stdoutReader = new StreamReader(stdoutPipe, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var stderrReader = new StreamReader(stderrPipe, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                // The Process wrapper owns drain orchestration, but these pipe/reader resources
                // are launcher-local and must be torn down after the pumps finish.
                var capturedStdoutReader = stdoutReader;
                var capturedStderrReader = stderrReader;
                var capturedStdoutPipe = stdoutPipe;
                var capturedStderrPipe = stderrPipe;
                var capturedProcessHandle = processHandle ?? throw new InvalidOperationException("Windows process handle was not initialized.");

                ValueTask ExtraDispose()
                {
                    try { capturedStdoutReader.Dispose(); } catch { }
                    try { capturedStderrReader.Dispose(); } catch { }
                    try { capturedStdoutPipe.Dispose(); } catch { }
                    try { capturedStderrPipe.Dispose(); } catch { }
                    // Closes the kept CreateProcess handle. Disposed after the wrapped Process so the
                    // override path stays live for the whole disposal window.
                    try { capturedProcessHandle.Dispose(); } catch { }
                    return ValueTask.CompletedTask;
                }

                // From here, pipe/reader/handle ownership has transferred to ExtraDispose;
                // clear the locals so the catch{} cleanup below doesn't double-dispose.
                stdoutPipe = null;
                stderrPipe = null;
                processHandle = null;

                return new StartedProcess(
                    process,
                    stdoutReader,
                    stderrReader,
                    ExtraDispose: ExtraDispose,
                    ExitCodeProvider: () => GetExitCode(capturedProcessHandle),
                    HasExitedProvider: () => GetHasExited(capturedProcessHandle),
                    WaitForExitProvider: ct => WaitForProcessHandleExitAsync(capturedProcessHandle, ct));
            }
            catch
            {
                // Anything between CreateProcess returning and the wrapper being handed off
                // failed — terminate the just-started child so we don't orphan it.
                try { WindowsProcessInterop.TerminateProcess(pi.hProcess, 1); } catch { }
                try { WindowsProcessInterop.CloseHandle(pi.hThread); } catch { }
                // Dispose the SafeProcessHandle if we still own it; if ownership already
                // transferred (processHandle == null) the wrapper's ExtraDispose owns it.
                processHandle?.Dispose();
                throw;
            }
        }
        catch
        {
            stdoutPipe?.Dispose();
            stderrPipe?.Dispose();
            // nulStdinHandle disposal: if we got past SetHandleInformation it's still alive;
            // if SetHandleInformation threw it's already failed. Dispose either way — it's
            // idempotent. (When success path reaches the inner try, it sets the local to
            // null-equivalent by calling Dispose() inline; the SafeFileHandle still tracks
            // disposed state so a second Dispose call is a no-op.)
            nulStdinHandle.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartedProcess StartWindowsSuppressed(IsolatedProcessStartInfo startInfo)
    {
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

        var pi = WindowsProcessInterop.SpawnProcess(
            startInfo.FileName,
            startInfo.ArgumentList,
            startInfo.WorkingDirectory,
            stdio,
            startInfo.GetEnvironmentForSpawn(),
            createNewConsole: startInfo.IsolateConsole,
            jobHandle: startInfo.KillOnParentExit ? WindowsConsoleProcessJob.Shared.Handle : null);

        SafeProcessHandle? processHandle = new(pi.hProcess, ownsHandle: true);
        try
        {
            var process = Process.GetProcessById(pi.dwProcessId);
            WindowsProcessInterop.CloseHandle(pi.hThread);
            pi.hThread = nint.Zero;

            var capturedProcessHandle = processHandle ?? throw new InvalidOperationException("Windows process handle was not initialized.");

            ValueTask ExtraDispose()
            {
                try { capturedProcessHandle.Dispose(); } catch { }
                return ValueTask.CompletedTask;
            }

            return new StartedProcess(
                process,
                TextReader.Null,
                TextReader.Null,
                ExtraDispose: ExtraDispose,
                ExitCodeProvider: () => GetExitCode(capturedProcessHandle),
                HasExitedProvider: () => GetHasExited(capturedProcessHandle),
                WaitForExitProvider: ct => WaitForProcessHandleExitAsync(capturedProcessHandle, ct));
        }
        catch
        {
            try { WindowsProcessInterop.TerminateProcess(pi.hProcess, 1); } catch { }
            if (pi.hThread != nint.Zero)
            {
                try { WindowsProcessInterop.CloseHandle(pi.hThread); } catch { }
            }
            processHandle?.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool GetHasExited(SafeProcessHandle processHandle)
    {
        ThrowIfDisposed(processHandle, nameof(HasExited));

        var waitResult = WindowsProcessInterop.WaitForSingleObject(processHandle, 0);
        return waitResult switch
        {
            WindowsProcessInterop.WaitObject0 => true,
            WindowsProcessInterop.WaitTimeout => false,
            WindowsProcessInterop.WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed while reading IsolatedProcess.HasExited"),
            _ => throw new InvalidOperationException($"Unexpected WaitForSingleObject result: 0x{waitResult:X8}"),
        };
    }

    [SupportedOSPlatform("windows")]
    private static int GetExitCode(SafeProcessHandle processHandle)
    {
        ThrowIfDisposed(processHandle, nameof(ExitCode));

        // Disambiguate STILL_ACTIVE (259) from a real 259 exit code via a zero-timeout wait.
        var waitResult = WindowsProcessInterop.WaitForSingleObject(processHandle, 0);
        if (waitResult == WindowsProcessInterop.WaitTimeout)
        {
            throw new InvalidOperationException("Process has not exited; cannot read ExitCode.");
        }
        if (waitResult == WindowsProcessInterop.WaitFailed)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed while reading IsolatedProcess.ExitCode");
        }

        if (!WindowsProcessInterop.GetExitCodeProcess(processHandle, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed while reading IsolatedProcess.ExitCode");
        }

        return unchecked((int)exitCode);
    }

    [SupportedOSPlatform("windows")]
    private static Task WaitForProcessHandleExitAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        ThrowIfDisposed(processHandle, nameof(WaitForExitAsync));
        return WindowsProcessInterop.WaitForExitAsync(processHandle, cancellationToken);
    }

    private static void ThrowIfDisposed(SafeProcessHandle processHandle, string memberName)
    {
        if (processHandle.IsClosed || processHandle.IsInvalid)
        {
            throw new InvalidOperationException($"Cannot read {memberName} after the IsolatedProcess has been disposed.");
        }
    }
}
