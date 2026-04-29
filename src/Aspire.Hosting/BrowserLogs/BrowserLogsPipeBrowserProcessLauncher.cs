// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Hosting;

// Starts Chromium with a private CDP pipe. This cannot use ProcessStartInfo today because the repo's target frameworks
// do not expose a supported way to map arbitrary child handles/fds. Chromium's pipe protocol has platform-specific
// launch contracts:
// - POSIX: the child must see the browser-input pipe at fd 3 and browser-output pipe at fd 4.
// - Windows: Chromium can adopt explicit inherited handles supplied through --remote-debugging-io-pipes=<read>,<write>.
//
// Keep all native launch details in this file so BrowserHost only deals with a process object and two ordinary streams.
internal static partial class BrowserLogsPipeBrowserProcessLauncher
{
    private const string RemoteDebuggingPipeArgument = "--remote-debugging-pipe";
    private static readonly TimeSpan s_processExitTimeout = TimeSpan.FromSeconds(5);

    public static IBrowserLogsPipeBrowserProcess Start(
        string executablePath,
        IReadOnlyList<string> browserArguments)
    {
        return OperatingSystem.IsWindows()
            ? StartWindows(executablePath, browserArguments)
            : StartPosix(executablePath, browserArguments);
    }

    internal static List<string> CreatePipeArguments(IReadOnlyList<string> browserArguments)
    {
        return [.. browserArguments, RemoteDebuggingPipeArgument];
    }

    private static BrowserLogsPipeBrowserProcess StartWindows(string executablePath, IReadOnlyList<string> browserArguments)
    {
        // Parent writes CDP commands to appToBrowser; the browser reads the client end. Parent reads responses/events
        // from browserToApp; the browser writes the client end. AnonymousPipeServerStream makes the client handles
        // inheritable, but CreateWindowsProcess below restricts inheritance to only those two handles.
        var appToBrowser = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var browserToApp = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        SafeWaitHandle? jobHandle = null;
        SafeWaitHandle? processHandle = null;
        try
        {
            var browserReadHandle = appToBrowser.GetClientHandleAsString();
            var browserWriteHandle = browserToApp.GetClientHandleAsString();
            var arguments = CreatePipeArguments(browserArguments);
            // Chromium expects the child read handle first and the child write handle second. These are raw Win32 handle
            // values, not file descriptor numbers, and Chromium opens them before starting DevTools pipe IO.
            arguments.Add($"--remote-debugging-io-pipes={browserReadHandle},{browserWriteHandle}");

            var inheritedHandles = new[]
            {
                new IntPtr(long.Parse(browserReadHandle, CultureInfo.InvariantCulture)),
                new IntPtr(long.Parse(browserWriteHandle, CultureInfo.InvariantCulture))
            };

            var processInfo = CreateWindowsProcess(executablePath, arguments, inheritedHandles);
            processHandle = new SafeWaitHandle(processInfo.ProcessHandle, ownsHandle: true);
            CloseWindowsHandle(processInfo.ThreadHandle);
            // A Windows job with KILL_ON_JOB_CLOSE gives pipe-created browsers the same "owned by the AppHost"
            // behavior even if the AppHost process exits before managed cleanup can run. Assigning can fail when a
            // parent job forbids nested jobs, so this remains best-effort and normal DisposeAsync cleanup is still
            // the primary path.
            jobHandle = TryCreateKillOnCloseJob(processHandle);

            appToBrowser.DisposeLocalCopyOfClientHandle();
            browserToApp.DisposeLocalCopyOfClientHandle();

            var processTask = WaitForWindowsProcessAsync(processHandle);
            return new BrowserLogsPipeBrowserProcess(
                processInfo.ProcessId,
                browserToApp,
                appToBrowser,
                processTask,
                new WindowsProcessLifetime(processInfo.ProcessId, processHandle, jobHandle, processTask));
        }
        catch
        {
            jobHandle?.Dispose();
            processHandle?.Dispose();
            appToBrowser.Dispose();
            browserToApp.Dispose();
            throw;
        }
    }

    private static BrowserLogsPipeBrowserProcess StartPosix(string executablePath, IReadOnlyList<string> browserArguments)
    {
        var appToBrowser = PosixPipe.Invalid;
        var browserToApp = PosixPipe.Invalid;
        FileStream? browserInput = null;
        FileStream? browserOutput = null;

        try
        {
            appToBrowser = CreatePosixPipe();
            browserToApp = CreatePosixPipe();
            // Chromium reserves fd 3 for browser input and fd 4 for browser output. pipe() can legally return either
            // number for one of our source descriptors, so move accidental fd 3/4 allocations out of the way before the
            // child remaps them with dup2. Without this, closing a "source" descriptor could accidentally close the final
            // reserved descriptor the browser needs.
            MoveReservedPipeDescriptors(ref appToBrowser);
            MoveReservedPipeDescriptors(ref browserToApp);

            var arguments = CreatePipeArguments(browserArguments);
            using var executablePathString = new NativeUtf8String(executablePath);
            using var argv = NativeStringArray.Create([executablePath, .. arguments]);

            var setParentDeathSignal = OperatingSystem.IsLinux();
            var processId = fork();
            if (processId == -1)
            {
                throw CreatePosixException("fork");
            }

            if (processId == 0)
            {
                // Keep the child path as small as possible after fork: remap descriptors, close extra pipe ends, and exec.
                // Do not log, allocate, or touch shared runtime state here. If any native call fails, exit immediately so
                // the parent observes process termination instead of a half-configured child.
                ExecPosixChild(
                    executablePathString.Pointer,
                    argv.Pointer,
                    appToBrowser.Read,
                    browserToApp.Write,
                    appToBrowser.Write,
                    browserToApp.Read,
                    setParentDeathSignal);
            }

            ClosePosixDescriptor(ref appToBrowser.Read);
            ClosePosixDescriptor(ref browserToApp.Write);

            // After the fork, the parent owns the write side of appToBrowser and the read side of browserToApp. Wrap them
            // in FileStream so the rest of BrowserLogs can treat pipe CDP like any other async stream transport.
            browserInput = CreateFileStreamFromDescriptor(ref appToBrowser.Write, FileAccess.Write);
            browserOutput = CreateFileStreamFromDescriptor(ref browserToApp.Read, FileAccess.Read);
            var processTask = WaitForPosixProcessAsync(processId);

            return new BrowserLogsPipeBrowserProcess(
                processId,
                browserOutput,
                browserInput,
                processTask,
                new PosixProcessLifetime(processId, processTask));
        }
        catch
        {
            browserInput?.Dispose();
            browserOutput?.Dispose();
            appToBrowser.Dispose();
            browserToApp.Dispose();
            throw;
        }
    }

    private static void ExecPosixChild(
        IntPtr executablePath,
        IntPtr argv,
        int browserReadDescriptor,
        int browserWriteDescriptor,
        int parentWriteDescriptor,
        int parentReadDescriptor,
        bool setParentDeathSignal)
    {
        if (setParentDeathSignal)
        {
            // Linux can ask the kernel to signal the browser when the AppHost process dies without running managed
            // cleanup. This is intentionally best-effort: macOS has no equivalent, and Chromium may still decide how to
            // cascade the signal to its child process tree.
            if (prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0) == -1 ||
                getppid() == 1)
            {
                _exit(127);
            }
        }

        if (dup2(browserReadDescriptor, 3) == -1 ||
            dup2(browserWriteDescriptor, 4) == -1)
        {
            _exit(127);
        }

        ClosePosixDescriptorIfNot(browserReadDescriptor, 3);
        ClosePosixDescriptorIfNot(browserWriteDescriptor, 4);
        close(parentWriteDescriptor);
        close(parentReadDescriptor);

        execv(executablePath, argv);
        _exit(127);
    }

    private static FileStream CreateFileStreamFromDescriptor(ref int descriptor, FileAccess access)
    {
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        descriptor = -1;
        // Anonymous pipe descriptors are not opened with overlapped/async flags. FileStream still exposes Task-based
        // methods over synchronous handles, but the constructor must be told the underlying handle is synchronous.
        return new FileStream(handle, access, bufferSize: 16 * 1024, isAsync: false);
    }

    private static PosixPipe CreatePosixPipe()
    {
        var descriptors = new int[2];
        if (pipe(descriptors) == -1)
        {
            throw CreatePosixException("pipe");
        }

        return new PosixPipe(descriptors[0], descriptors[1]);
    }

    private static void MoveReservedPipeDescriptors(ref PosixPipe pipe)
    {
        pipe.Read = MoveReservedDescriptor(pipe.Read);
        pipe.Write = MoveReservedDescriptor(pipe.Write);
    }

    private static int MoveReservedDescriptor(int descriptor)
    {
        if (descriptor is not (3 or 4))
        {
            return descriptor;
        }

        var movedDescriptor = fcntl(descriptor, F_DUPFD, 5);
        if (movedDescriptor == -1)
        {
            throw CreatePosixException("fcntl");
        }

        close(descriptor);
        return movedDescriptor;
    }

    private static async Task<ProcessResult> WaitForPosixProcessAsync(int processId)
    {
        return await Task.Run(() =>
        {
            while (true)
            {
                var result = waitpid(processId, out var status, 0);
                if (result == processId)
                {
                    return new ProcessResult(GetPosixExitCode(status));
                }

                if (Marshal.GetLastPInvokeError() != EINTR)
                {
                    throw CreatePosixException("waitpid");
                }
            }
        }).ConfigureAwait(false);
    }

    private static int GetPosixExitCode(int status)
    {
        if ((status & 0x7f) == 0)
        {
            return (status >> 8) & 0xff;
        }

        return 128 + (status & 0x7f);
    }

    private static void ClosePosixDescriptor(ref int descriptor)
    {
        if (descriptor >= 0)
        {
            close(descriptor);
            descriptor = -1;
        }
    }

    private static void ClosePosixDescriptorIfNot(int descriptor, int reservedDescriptor)
    {
        if (descriptor != reservedDescriptor)
        {
            close(descriptor);
        }
    }

    private static Win32Exception CreatePosixException(string operation) =>
        new(Marshal.GetLastPInvokeError(), $"Failed to invoke {operation} while starting tracked browser CDP pipe.");

    private static WindowsProcessInfo CreateWindowsProcess(string executablePath, IReadOnlyList<string> arguments, IntPtr[] inheritedHandles)
    {
        var commandLine = Marshal.StringToHGlobalUni(BuildWindowsCommandLine(executablePath, arguments));
        var attributeListSize = UIntPtr.Zero;
        // STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST lets us turn on handle inheritance while limiting it to the
        // two CDP pipe handles. That avoids the broad "all inheritable handles leak into Chromium" behavior of plain
        // CreateProcess(..., inheritHandles: true).
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal((nint)attributeListSize.ToUInt64());
        var handleList = Marshal.AllocHGlobal(IntPtr.Size * inheritedHandles.Length);
        var startupInfoPointer = IntPtr.Zero;

        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw CreateWindowsException("InitializeProcThreadAttributeList");
            }

            for (var i = 0; i < inheritedHandles.Length; i++)
            {
                Marshal.WriteIntPtr(handleList, i * IntPtr.Size, inheritedHandles[i]);
            }

            if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                s_procThreadAttributeHandleList,
                handleList,
                (UIntPtr)(uint)(IntPtr.Size * inheritedHandles.Length),
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw CreateWindowsException("UpdateProcThreadAttribute");
            }

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>()
                },
                lpAttributeList = attributeList
            };
            startupInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<STARTUPINFOEX>());
            Marshal.StructureToPtr(startupInfo, startupInfoPointer, fDeleteOld: false);

            if (!CreateProcessW(
                lpApplicationName: IntPtr.Zero,
                lpCommandLine: commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: true,
                dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: IntPtr.Zero,
                lpStartupInfo: startupInfoPointer,
                lpProcessInformation: out var processInformation))
            {
                throw CreateWindowsException("CreateProcessW");
            }

            return new WindowsProcessInfo(processInformation.dwProcessId, processInformation.hProcess, processInformation.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(startupInfoPointer);
            Marshal.FreeHGlobal(attributeList);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(commandLine);
        }
    }

    internal static string BuildWindowsCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendWindowsCommandLineArgument(builder, executablePath);

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendWindowsCommandLineArgument(builder, argument);
        }

        return builder.ToString();
    }

    // Adapted from dotnet/runtime PasteArguments.AppendArgument so CreateProcess receives the same argv Chromium expects.
    private static void AppendWindowsCommandLineArgument(StringBuilder builder, string argument)
    {
        if (argument.Length != 0 && !argument.AsSpan().ContainsAny(' ', '\t', '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        var index = 0;
        while (index < argument.Length)
        {
            var character = argument[index++];
            if (character == '\\')
            {
                var backslashCount = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashCount++;
                }

                if (index == argument.Length)
                {
                    builder.Append('\\', backslashCount * 2);
                }
                else if (argument[index] == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    index++;
                }
                else
                {
                    builder.Append('\\', backslashCount);
                }

                continue;
            }

            if (character == '"')
            {
                builder.Append('\\');
                builder.Append('"');
                continue;
            }

            builder.Append(character);
        }

        builder.Append('"');
    }

    private static async Task<ProcessResult> WaitForWindowsProcessAsync(SafeWaitHandle processHandle)
    {
        return await Task.Run(() =>
        {
            var waitResult = WaitForSingleObject(processHandle.DangerousGetHandle(), INFINITE);
            if (waitResult != WAIT_OBJECT_0)
            {
                throw CreateWindowsException("WaitForSingleObject");
            }

            if (!GetExitCodeProcess(processHandle.DangerousGetHandle(), out var exitCode))
            {
                throw CreateWindowsException("GetExitCodeProcess");
            }

            return new ProcessResult(unchecked((int)exitCode));
        }).ConfigureAwait(false);
    }

    private static Win32Exception CreateWindowsException(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Failed to invoke {operation} while starting tracked browser CDP pipe.");

    private static void CloseWindowsHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && !CloseHandle(handle))
        {
            throw CreateWindowsException("CloseHandle");
        }
    }

    private static void TryKillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static SafeWaitHandle? TryCreateKillOnCloseJob(SafeWaitHandle processHandle)
    {
        var job = CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (job == IntPtr.Zero)
        {
            return null;
        }

        var jobHandle = new SafeWaitHandle(job, ownsHandle: true);
        var jobInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };
        var jobInfoSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var jobInfoPointer = Marshal.AllocHGlobal(jobInfoSize);

        try
        {
            Marshal.StructureToPtr(jobInfo, jobInfoPointer, fDeleteOld: false);
            if (!SetInformationJobObject(jobHandle.DangerousGetHandle(), JobObjectExtendedLimitInformation, jobInfoPointer, (uint)jobInfoSize) ||
                !AssignProcessToJobObject(jobHandle.DangerousGetHandle(), processHandle.DangerousGetHandle()))
            {
                jobHandle.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(jobInfoPointer);
        }

        return jobHandle;
    }

    private readonly record struct WindowsProcessInfo(int ProcessId, IntPtr ProcessHandle, IntPtr ThreadHandle);

    private struct PosixPipe(int read, int write) : IDisposable
    {
        public static PosixPipe Invalid => new(-1, -1);

        public int Read = read;

        public int Write = write;

        public void Dispose()
        {
            ClosePosixDescriptor(ref Read);
            ClosePosixDescriptor(ref Write);
        }
    }

    private sealed class PosixProcessLifetime(int processId, Task<ProcessResult> processTask) : IBrowserLogsPipeBrowserProcessLifetime
    {
        public async ValueTask DisposeAsync()
        {
            if (processTask.IsCompleted)
            {
                return;
            }

            sys_kill(processId, SIGINT);
            try
            {
                await processTask.WaitAsync(s_processExitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                sys_kill(processId, SIGKILL);
                await processTask.ConfigureAwait(false);
            }
        }
    }

    private sealed class WindowsProcessLifetime(int processId, SafeWaitHandle processHandle, SafeWaitHandle? jobHandle, Task<ProcessResult> processTask) : IBrowserLogsPipeBrowserProcessLifetime
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!processTask.IsCompleted)
                {
                    TryKillProcessTree(processId);
                    await processTask.WaitAsync(s_processExitTimeout).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                TryKillProcessTree(processId);
                await processTask.ConfigureAwait(false);
            }
            finally
            {
                jobHandle?.Dispose();
                processHandle.Dispose();
            }
        }
    }

    private sealed class NativeUtf8String : IDisposable
    {
        public NativeUtf8String(string value)
        {
            Pointer = Marshal.StringToCoTaskMemUTF8(value);
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    private sealed class NativeStringArray : IDisposable
    {
        private readonly NativeUtf8String[] _strings;

        private NativeStringArray(NativeUtf8String[] strings, IntPtr pointer)
        {
            _strings = strings;
            Pointer = pointer;
        }

        public IntPtr Pointer { get; }

        public static NativeStringArray Create(IReadOnlyList<string> values)
        {
            var strings = values.Select(static value => new NativeUtf8String(value)).ToArray();
            var pointer = Marshal.AllocHGlobal(IntPtr.Size * (strings.Length + 1));

            for (var i = 0; i < strings.Length; i++)
            {
                Marshal.WriteIntPtr(pointer, i * IntPtr.Size, strings[i].Pointer);
            }

            Marshal.WriteIntPtr(pointer, strings.Length * IntPtr.Size, IntPtr.Zero);
            return new NativeStringArray(strings, pointer);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            foreach (var value in _strings)
            {
                value.Dispose();
            }
        }
    }

    private const int CREATE_NO_WINDOW = 0x08000000;
    private const int EINTR = 4;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int F_DUPFD = 0;
    private const uint INFINITE = 0xffffffff;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int PR_SET_PDEATHSIG = 1;
    private static readonly IntPtr s_procThreadAttributeHandleList = 0x00020002;
    private const int SIGINT = 2;
    private const int SIGKILL = 9;
    private const int SIGTERM = 15;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        IntPtr lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)]
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        IntPtr lpCurrentDirectory,
        IntPtr lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldfd, int newfd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "execv")]
    private static partial int execv(IntPtr path, IntPtr argv);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fork();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getppid();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref UIntPtr lpSize);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pipe([Out] int[] pipefd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, int arg2, int arg3, int arg4, int arg5);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static partial int sys_kill(int pid, int sig);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        int dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        UIntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "_exit")]
    private static partial void _exit(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }
}
