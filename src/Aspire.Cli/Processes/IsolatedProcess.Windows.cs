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
    /// then delegates to <see cref="WindowsProcessInterop.SpawnConsoleIsolatedProcess"/> for
    /// the actual <c>CreateProcessW</c> ceremony. When <see cref="IsolatedProcessStartInfo.JobHandle"/>
    /// is supplied, the spawn primitive does the suspended-create / assign / resume dance so
    /// the child cannot escape the CLI's kill-on-close job between spawn and assignment.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="DetachedProcessLauncher"/>, this launcher consumes the child's
    /// stdout/stderr line-by-line via anonymous pipes. stdin is wired to NUL because we
    /// don't supply input to the interactive child, but Windows still requires a valid
    /// handle when STARTF_USESTDHANDLES is set and the other two stdio handles are real
    /// pipes — passing IntPtr.Zero in that combination leaves child stdin referencing
    /// whatever default the loader picks, which has tripped up some test runners in the
    /// past.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static IsolatedProcess StartWindows(
        IsolatedProcessStartInfo startInfo,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler)
    {
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

            var pi = WindowsProcessInterop.SpawnConsoleIsolatedProcess(
                startInfo.FileName,
                startInfo.ArgumentList,
                startInfo.WorkingDirectory,
                stdio,
                environment,
                startInfo.JobHandle);

            // CreateProcess succeeded; from here, any failure must terminate the just-created
            // child instead of letting it run orphaned. Drop the parent-side copy of the
            // client write ends so EOF reaches the StreamReader pumps when the child closes
            // its handle on exit — without this, the pump would never see EOF and disposal
            // would always have to wait for the drain timeout.

            // Take ownership of pi.hProcess in a SafeProcessHandle FIRST so that any failure
            // below (including OOM in the inner try) cannot leak the raw handle — the
            // SafeProcessHandle finalizer will close it. We keep this handle for the lifetime
            // of the IsolatedProcess so that ExitCode / HasExited can query the child via
            // GetExitCodeProcess / WaitForSingleObject directly. Process objects obtained via
            // Process.GetProcessById cannot reliably surface ExitCode on Windows — see
            // https://github.com/dotnet/runtime/issues/45003. Holding the original
            // CreateProcess handle also pins the OS process object so a recycled PID cannot
            // redirect GetProcessById to a different process during the brief window between
            // CreateProcess returning and GetProcessById running.
            SafeProcessHandle? processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
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

                // UTF-8 with non-throwing fallback — a stray OEM-encoded byte from a tsx
                // warning shouldn't kill the pump. Mojibake is the documented tradeoff.
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                var stdoutReader = new StreamReader(stdoutPipe, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var stderrReader = new StreamReader(stderrPipe, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                // Capture these for the extraDispose closure below — the shared
                // WrapStartedProcess helper owns the Process and the drain orchestration,
                // but the pipe/reader resources are launcher-local and must be torn down
                // after the pumps finish.
                var capturedStdoutReader = stdoutReader;
                var capturedStderrReader = stderrReader;
                var capturedStdoutPipe = stdoutPipe;
                var capturedStderrPipe = stderrPipe;
                var capturedProcessHandle = processHandle;

                ValueTask ExtraDispose()
                {
                    try { capturedStdoutReader.Dispose(); } catch { }
                    try { capturedStderrReader.Dispose(); } catch { }
                    try { capturedStdoutPipe.Dispose(); } catch { }
                    try { capturedStderrPipe.Dispose(); } catch { }
                    // Closes the kept CreateProcess handle. Disposed after the wrapped
                    // Process so any final ExitCode read from inside Process.Dispose
                    // semantics can still consult our override; not that we expect it to,
                    // but the ordering keeps the override path live for the whole disposal
                    // window.
                    try { capturedProcessHandle.Dispose(); } catch { }
                    return ValueTask.CompletedTask;
                }

                // ExitCode/HasExited overrides for IsolatedProcess.cs. The handle reference
                // is the captured local — by the time these closures run, ExtraDispose may
                // also be queued, but the closures and ExtraDispose share a single
                // SafeProcessHandle whose Dispose state both can observe. After the wrapper
                // is disposed, calls to ExitCode/HasExited will throw via the
                // SafeHandle-closed path, which matches the contract that the wrapper is
                // unusable after disposal.
                int GetExitCode()
                {
                    if (capturedProcessHandle.IsClosed || capturedProcessHandle.IsInvalid)
                    {
                        throw new InvalidOperationException("Cannot read ExitCode after the IsolatedProcess has been disposed.");
                    }

                    // Disambiguate STILL_ACTIVE (259) from a real 259 exit code via a
                    // zero-timeout wait. The handle was opened by CreateProcess with full
                    // access including SYNCHRONIZE, so WaitForSingleObject succeeds.
                    var waitResult = WindowsProcessInterop.WaitForSingleObject(capturedProcessHandle, 0);
                    if (waitResult == WindowsProcessInterop.WaitTimeout)
                    {
                        throw new InvalidOperationException("Process has not exited; cannot read ExitCode.");
                    }
                    if (waitResult == WindowsProcessInterop.WaitFailed)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed while reading IsolatedProcess.ExitCode");
                    }

                    if (!WindowsProcessInterop.GetExitCodeProcess(capturedProcessHandle, out var exitCode))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed while reading IsolatedProcess.ExitCode");
                    }
                    return unchecked((int)exitCode);
                }

                bool GetHasExited()
                {
                    if (capturedProcessHandle.IsClosed || capturedProcessHandle.IsInvalid)
                    {
                        // After dispose, mirror Process.HasExited's behavior of throwing —
                        // callers shouldn't be querying a disposed wrapper.
                        throw new InvalidOperationException("Cannot read HasExited after the IsolatedProcess has been disposed.");
                    }

                    var waitResult = WindowsProcessInterop.WaitForSingleObject(capturedProcessHandle, 0);
                    return waitResult switch
                    {
                        WindowsProcessInterop.WaitObject0 => true,
                        WindowsProcessInterop.WaitTimeout => false,
                        WindowsProcessInterop.WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed while reading IsolatedProcess.HasExited"),
                        _ => throw new InvalidOperationException($"Unexpected WaitForSingleObject result: 0x{waitResult:X8}"),
                    };
                }

                // From here, pipe/reader/handle ownership has transferred to ExtraDispose;
                // clear the locals so the catch{} cleanup below doesn't double-dispose.
                stdoutPipe = null;
                stderrPipe = null;
                processHandle = null;

                return WrapStartedProcess(
                    startInfo,
                    process,
                    stdoutReader,
                    stderrReader,
                    standardOutputHandler,
                    standardErrorHandler,
                    ExtraDispose,
                    exitCodeProvider: GetExitCode,
                    hasExitedProvider: GetHasExited,
                    waitForExitProvider: ct => WindowsProcessInterop.WaitForExitAsync(capturedProcessHandle, ct));
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
}
