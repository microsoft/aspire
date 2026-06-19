// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Processes;

/// <summary>
/// Helpers and Win32 interop declarations shared by Windows process launchers that hand off
/// raw command lines, environment blocks, and STARTUPINFO structures to <c>CreateProcessW</c>.
/// Both <see cref="DetachedProcessLauncher"/> and <see cref="IsolatedProcess"/>
/// open the same console-isolation flags, attribute-list shape, and stdio-handle plumbing, so
/// the constants, structs, and P/Invoke declarations live here to prevent the two callers from
/// silently drifting apart on something like "this one accidentally lacks
/// <c>CREATE_UNICODE_ENVIRONMENT</c>" or "these struct layouts diverged after a Win32 SDK update".
/// </summary>
internal static partial class WindowsProcessInterop
{
    // === Constants ===
    // See https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew for the
    // dwDesiredAccess / dwShareMode / dwCreationDisposition flag values, and
    // https://learn.microsoft.com/windows/win32/procthread/process-creation-flags for the
    // creation flags consumed by CreateProcessW.

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;

    public const uint HandleFlagInherit = 0x00000001;

    public const uint StartfUseStdHandles = 0x00000100;
    public const uint StartfUseShowWindow = 0x00000001;

    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateNewConsole = 0x00000010;

    /// <summary>
    /// Composite creation flags shared by every CLI process launcher that spawns into its own
    /// hidden console group: CREATE_UNICODE_ENVIRONMENT (we always build a Unicode env block
    /// ourselves) | EXTENDED_STARTUPINFO_PRESENT (we always pass STARTUPINFOEX with an attribute
    /// list) | CREATE_NEW_CONSOLE (the entire point — detach from the parent's console).
    /// Centralizing the composite prevents the two launchers from drifting (e.g. one path
    /// accidentally dropping CREATE_UNICODE_ENVIRONMENT and silently truncating non-ASCII env
    /// values).
    /// </summary>
    public const uint NewConsoleCreationFlags =
        CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNewConsole;

    public const ushort ShowWindowHide = 0x0000;

    // PROC_THREAD_ATTRIBUTE_HANDLE_LIST — see
    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
    public static readonly nint ProcThreadAttributeHandleList = (nint)0x00020002;

    // === Structs ===

    /// <summary>
    /// STARTUPINFOEX — see
    /// https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-startupinfoexw.
    /// Layout-equivalent to STARTUPINFOW with a trailing PPROC_THREAD_ATTRIBUTE_LIST pointer.
    /// We always use this variant (not plain STARTUPINFOW) because both launchers pass
    /// EXTENDED_STARTUPINFO_PRESENT and PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
        public nint lpAttributeList;
    }

    /// <summary>
    /// PROCESS_INFORMATION — see
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_information.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // === P/Invoke declarations ===

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    // CreateProcessW must remain on DllImport (not LibraryImport): the source generator does
    // not produce a marshaller for mutable StringBuilder command-line buffers, and Win32
    // requires lpCommandLine to point at writable memory. See
    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
#pragma warning disable CA1838 // CreateProcessW requires a mutable command line buffer
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
#pragma warning restore CA1838

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint hThread);

    // GetExitCodeProcess + WaitForSingleObject are used by IsolatedProcess.Windows so that
    // IsolatedProcess.ExitCode / HasExited can query the child via the SafeProcessHandle we
    // kept open from CreateProcessW. Process objects obtained via Process.GetProcessById
    // cannot reliably surface ExitCode on Windows ("Process was not started by this object"
    // InvalidOperationException) — see https://github.com/dotnet/runtime/issues/45003. By
    // holding the original CreateProcess handle and calling Win32 directly we sidestep the
    // managed-Process state machine that depends on Process.Start having been the producer.
    // Docs:
    //   https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess
    //   https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    // GetExitCodeProcess returns STILL_ACTIVE (259) when the process is still running, but
    // a process can also legitimately exit with code 259. Use WaitForSingleObject with a
    // zero timeout to disambiguate: WAIT_OBJECT_0 means signaled (truly exited), WAIT_TIMEOUT
    // means still running. See the GetExitCodeProcess remarks in the docs linked above.
    public const uint StillActive = 259;
    public const uint WaitObject0 = 0x00000000;
    public const uint WaitTimeout = 0x00000102;
    public const uint WaitFailed = 0xFFFFFFFF;

    // Job-object APIs — see
    // https://learn.microsoft.com/windows/win32/procthread/job-objects. We use a job to
    // guarantee that interactive children (and their grandchildren) are killed when the CLI
    // process exits (clean or crash), so an orphaned guest AppHost in its own console group
    // can't survive a parent SIGKILL/segfault and leak. DCP is expected to use
    // CREATE_BREAKAWAY_FROM_JOB to escape this job before the kill fires.

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(
        SafeFileHandle hJob,
        JobObjectInfoClass JobObjectInformationClass,
        nint lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeFileHandle hJob, nint hProcess);

    // SetConsoleCtrlHandler — see
    // https://learn.microsoft.com/windows/console/setconsolectrlhandler
    // Passing NULL as the handler routine controls the inherited "ignore CTRL+C" attribute that
    // the kernel propagates across CreateProcess: SetConsoleCtrlHandler(NULL, TRUE) disables
    // CTRL+C for the calling process (this is exactly what CREATE_NEW_PROCESS_GROUP does to a
    // new root process), and SetConsoleCtrlHandler(NULL, FALSE) re-enables it. Subsequently
    // spawned children inherit the new state, so calling FALSE early in CLI startup ensures
    // the AppHost and any DCP-launched services see CTRL+C even when the CLI itself was
    // launched as a descendant of a NEW_PROCESS_GROUP root.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCtrlHandler(nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    // === Job-object constants ===

    public const uint CreateSuspended = 0x00000004;

    // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information
    public const uint JobObjectLimitBreakawayOk = 0x00000800;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    /// <summary>
    /// JOBOBJECTINFOCLASS values consumed by SetInformationJobObject. We currently use only
    /// <see cref="ExtendedLimitInformation"/>; the rest are intentionally omitted until needed.
    /// See https://learn.microsoft.com/windows/win32/api/winnt/ne-winnt-jobobjectinfoclass.
    /// </summary>
    public enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9,
    }

    // === Job-object structs ===

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>
    /// Per-spawn stdio handle layout — the three handles that will be inherited by the child
    /// via <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>. A handle of <see langword="nint.Zero" />
    /// means "do not assign this slot" (Windows will treat it as no stdio for that fd) and
    /// causes the slot to be skipped in the inheritance whitelist.
    /// </summary>
    public readonly record struct StdioHandles(nint Stdin, nint Stdout, nint Stderr);

    /// <summary>
    /// Spawns a child process in its own hidden console group with exactly the stdio handles
    /// in <paramref name="stdio"/> made inheritable through <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>.
    /// Used by both <see cref="DetachedProcessLauncher"/> (NUL-only handles) and
    /// <see cref="IsolatedProcess"/> (NUL stdin + anonymous pipes for stdout/stderr)
    /// so the console-isolation ceremony lives in one place.
    /// </summary>
    /// <param name="fileName">Full path to the executable to launch.</param>
    /// <param name="arguments">Arguments to pass to the child. Quoted via <see cref="BuildCommandLine"/>.</param>
    /// <param name="workingDirectory">Working directory for the child.</param>
    /// <param name="stdio">
    /// Stdio handle slots. Each non-zero slot is wired to the corresponding child handle and
    /// added to the inheritance whitelist; zero slots are skipped (e.g. detached children leave
    /// <c>Stdin</c> as <c>nint.Zero</c>).
    /// </param>
    /// <param name="environment">
    /// Optional complete environment for the child. When <see langword="null"/>, the parent's
    /// environment is inherited verbatim (no env block allocated). When non-null, the dictionary
    /// supplies the entire child env block — entries with <see langword="null"/> values are
    /// omitted (matches <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> semantics). Callers that want
    /// to remove a subset of parent variables must materialize the parent env, apply their
    /// removals/overlays, and pass the resulting dictionary here.
    /// </param>
    /// <param name="jobHandle">
    /// Optional kill-on-close job object. When supplied, the child is created suspended,
    /// assigned to the job, then resumed — so there is no instruction-level window where the
    /// child could spawn a grandchild that escapes the job. <see cref="DetachedProcessLauncher"/>
    /// passes <see langword="null" /> because detached children must outlive the CLI;
    /// <see cref="IsolatedProcess"/> passes the singleton CLI job so children
    /// die with a parent crash.
    /// </param>
    /// <returns>
    /// The raw <see cref="PROCESS_INFORMATION"/> from <c>CreateProcessW</c>. The caller owns
    /// <c>hProcess</c> and <c>hThread</c> and must close both once it has extracted whatever
    /// it needs (e.g. obtained a managed <see cref="System.Diagnostics.Process"/> handle via
    /// <see cref="System.Diagnostics.Process.GetProcessById(int)"/>).
    /// </returns>
    [SupportedOSPlatform("windows")]
    public static PROCESS_INFORMATION SpawnConsoleIsolatedProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        StdioHandles stdio,
        IReadOnlyDictionary<string, string?>? environment,
        SafeFileHandle? jobHandle)
    {
        // Build the handle whitelist from the non-zero stdio slots. We pass exactly the handles
        // the child is supposed to inherit and nothing else — this is the entire point of
        // PROC_THREAD_ATTRIBUTE_HANDLE_LIST: deny inheritance of every other inheritable handle
        // open on any parent thread (DCP socket fds, pipe fds, etc.).
        //
        // The whitelist MUST NOT contain duplicate handle values. PROC_THREAD_ATTRIBUTE_HANDLE_LIST
        // is documented to reject duplicates and CreateProcessW returns ERROR_INVALID_PARAMETER
        // (87) if any handle appears more than once. DetachedProcessLauncher legitimately points
        // both Stdout and Stderr at the same NUL handle (child writes go nowhere), so we
        // de-duplicate by handle value before populating the attribute. See:
        // https://devblogs.microsoft.com/oldnewthing/20111216-00/?p=8873
        var inheritable = new List<nint>(3);
        void AddIfUnique(nint handle)
        {
            if (handle != nint.Zero && !inheritable.Contains(handle))
            {
                inheritable.Add(handle);
            }
        }
        AddIfUnique(stdio.Stdin);
        AddIfUnique(stdio.Stdout);
        AddIfUnique(stdio.Stderr);

        var attrListSize = nint.Zero;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attrListSize);

        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize process thread attribute list");
            }

            try
            {
                var handles = inheritable.ToArray();
                var pinnedHandles = GCHandle.Alloc(handles, GCHandleType.Pinned);
                try
                {
                    if (!UpdateProcThreadAttribute(
                        attrList,
                        0,
                        ProcThreadAttributeHandleList,
                        pinnedHandles.AddrOfPinnedObject(),
                        (nint)(nint.Size * handles.Length),
                        nint.Zero,
                        nint.Zero))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to update process thread attribute list");
                    }

                    var si = new STARTUPINFOEX
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEX>(),
                        dwFlags = StartfUseStdHandles | StartfUseShowWindow,
                        hStdInput = stdio.Stdin,
                        hStdOutput = stdio.Stdout,
                        hStdError = stdio.Stderr,
                        lpAttributeList = attrList,
                        // CREATE_NO_WINDOW is ignored with CREATE_NEW_CONSOLE; SW_HIDE keeps
                        // the new console window off-screen.
                        wShowWindow = ShowWindowHide,
                    };

                    var commandLine = BuildCommandLine(fileName, arguments);

                    // Only suspend when a job is involved: we need the child frozen between
                    // CreateProcess and AssignProcessToJobObject so it cannot fork-and-breakaway
                    // before we put the safety net under it. Without a job, the original
                    // behavior is preserved bit-for-bit (no suspend, no resume).
                    var flags = NewConsoleCreationFlags;
                    if (jobHandle is not null)
                    {
                        flags |= CreateSuspended;
                    }

                    var envBlockHandle = nint.Zero;
                    try
                    {
                        if (environment is not null)
                        {
                            envBlockHandle = BuildEnvironmentBlock(environment);
                        }

                        if (!CreateProcessW(
                            null,
                            commandLine,
                            nint.Zero,
                            nint.Zero,
                            bInheritHandles: true,
                            flags,
                            envBlockHandle,
                            workingDirectory,
                            ref si,
                            out var pi))
                        {
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Failed to create process: {fileName}");
                        }

                        if (jobHandle is not null)
                        {
                            // Wrap the post-CreateProcess steps so any failure between here and
                            // ResumeThread kills the suspended child — otherwise we'd leak a
                            // process stuck in initial-suspend state.
                            try
                            {
                                if (!AssignProcessToJobObject(jobHandle, pi.hProcess))
                                {
                                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to assign child process to job object");
                                }

                                // ResumeThread returns the previous suspend count, or 0xFFFFFFFF on failure.
                                if (ResumeThread(pi.hThread) == uint.MaxValue)
                                {
                                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to resume suspended child thread");
                                }
                            }
                            catch
                            {
                                try { TerminateProcess(pi.hProcess, 1); } catch { }
                                try { CloseHandle(pi.hThread); } catch { }
                                try { CloseHandle(pi.hProcess); } catch { }
                                throw;
                            }
                        }

                        return pi;
                    }
                    finally
                    {
                        if (envBlockHandle != nint.Zero)
                        {
                            Marshal.FreeHGlobal(envBlockHandle);
                        }
                    }
                }
                finally
                {
                    pinnedHandles.Free();
                }
            }
            finally
            {
                DeleteProcThreadAttributeList(attrList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attrList);
        }
    }

    /// <summary>
    /// Builds a Windows command line string with correct quoting rules.
    /// Adapted from dotnet/runtime PasteArguments.AppendArgument.
    /// </summary>
    public static StringBuilder BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();

        sb.Append('"').Append(fileName).Append('"');

        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }

        return sb;
    }

    /// <summary>
    /// Appends a correctly-quoted argument to the command line.
    /// Copied from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/PasteArguments.cs
    /// </summary>
    public static void AppendArgument(StringBuilder sb, string argument)
    {
        // Windows command-line parsing rules:
        //   - Backslash is normal except when followed by a quote
        //   - 2N backslashes + quote → N literal backslashes + unescaped quote
        //   - 2N+1 backslashes + quote → N literal backslashes + literal quote
        if (argument.Length != 0 && !argument.AsSpan().ContainsAny(' ', '\t', '"'))
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var idx = 0;
        while (idx < argument.Length)
        {
            var c = argument[idx++];
            if (c == '\\')
            {
                var numBackslash = 1;
                while (idx < argument.Length && argument[idx] == '\\')
                {
                    idx++;
                    numBackslash++;
                }

                if (idx == argument.Length)
                {
                    // Trailing backslashes before closing quote — must double them
                    sb.Append('\\', numBackslash * 2);
                }
                else if (argument[idx] == '"')
                {
                    // Backslashes followed by quote — double them + escape the quote
                    sb.Append('\\', numBackslash * 2 + 1);
                    sb.Append('"');
                    idx++;
                }
                else
                {
                    // Backslashes not followed by quote — emit as-is
                    sb.Append('\\', numBackslash);
                }

                continue;
            }

            if (c == '"')
            {
                sb.Append('\\');
                sb.Append('"');
                continue;
            }

            sb.Append(c);
        }

        sb.Append('"');
    }

    /// <summary>
    /// Builds a Unicode environment block for CreateProcessW from a fully-resolved
    /// environment dictionary. The block is sorted by variable name (case-insensitive,
    /// as required by Windows) and double-null-terminated. The caller must free the
    /// returned pointer with Marshal.FreeHGlobal.
    /// </summary>
    /// <param name="environment">
    /// The complete environment for the child. Entries with a <see langword="null"/>
    /// value are omitted (mirrors <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> semantics).
    /// </param>
    [SupportedOSPlatform("windows")]
    public static nint BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> environment)
    {
        var envVars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment)
        {
            if (value is not null)
            {
                envVars[key] = value;
            }
        }

        // Build the double-null-terminated Unicode environment block:
        // KEY1=VALUE1\0KEY2=VALUE2\0...\0\0
        var blockBuilder = new StringBuilder();
        foreach (var kvp in envVars)
        {
            blockBuilder.Append(kvp.Key);
            blockBuilder.Append('=');
            blockBuilder.Append(kvp.Value);
            blockBuilder.Append('\0');
        }

        if (envVars.Count == 0)
        {
            blockBuilder.Append('\0');
        }

        blockBuilder.Append('\0');

        var blockString = blockBuilder.ToString();
        var byteCount = Encoding.Unicode.GetByteCount(blockString);
        var ptr = Marshal.AllocHGlobal(byteCount);
        unsafe
        {
            fixed (char* pStr = blockString)
            {
                Encoding.Unicode.GetBytes(pStr, blockString.Length, (byte*)ptr, byteCount);
            }
        }

        return ptr;
    }

    /// <summary>
    /// Asynchronously waits for the process owned by <paramref name="processHandle"/> to exit by
    /// registering a thread-pool wait on the kernel process object.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Diagnostics.Process.WaitForExitAsync(CancellationToken)"/> on a
    /// <see cref="System.Diagnostics.Process.GetProcessById(int)"/> instance is unreliable on
    /// Windows (see https://github.com/dotnet/runtime/issues/45003): it can complete before the
    /// kernel has marked the process exited, so a subsequent <c>GetExitCodeProcess</c> read still
    /// reports <c>STILL_ACTIVE</c>. This waits on the kept <c>CreateProcess</c> handle — the same
    /// authoritative source <see cref="GetExitCodeProcess"/> and the zero-timeout
    /// <see cref="WaitForSingleObject"/> read from — so when this completes an ExitCode read is
    /// guaranteed to succeed.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static Task WaitForExitAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (processHandle.IsClosed || processHandle.IsInvalid)
        {
            // No live handle to wait on (the wrapper disposed it); the process is gone as far as we
            // can observe, so mirror a completed wait rather than throwing.
            return Task.CompletedTask;
        }

        // Fast path: already signaled, so skip the thread-pool registration entirely.
        if (WaitForSingleObject(processHandle, 0) == WaitObject0)
        {
            return Task.CompletedTask;
        }

        return WaitForExitCoreAsync(processHandle, cancellationToken);

        static async Task WaitForExitCoreAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken)
        {
            // Pin the SafeProcessHandle for the duration of the wait so a concurrent Dispose (the
            // wrapper's ExtraDispose closes this handle) cannot recycle the raw handle out from
            // under the registered thread-pool wait. If the handle was closed in the meantime,
            // DangerousAddRef throws ObjectDisposedException — treat that as "already gone".
            var added = false;
            try
            {
                processHandle.DangerousAddRef(ref added);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // RegisterWaitForSingleObject needs a managed WaitHandle. Wrap the raw process handle in
            // a non-owning SafeWaitHandle (the SafeProcessHandle keeps ownership) and graft it onto a
            // throwaway ManualResetEvent — the canonical way to wait on a foreign kernel handle.
            var waitHandle = new ManualResetEvent(false);
            var placeholder = waitHandle.SafeWaitHandle;
            waitHandle.SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
            placeholder.Dispose();

            RegisteredWaitHandle? registration = null;
            var ctr = cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
            try
            {
                registration = ThreadPool.RegisterWaitForSingleObject(
                    waitHandle,
                    static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                    tcs,
                    millisecondsTimeOutInterval: Timeout.Infinite,
                    executeOnlyOnce: true);

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                ctr.Dispose();
                registration?.Unregister(null);
                waitHandle.Dispose();
                if (added)
                {
                    processHandle.DangerousRelease();
                }
            }
        }
    }
}
