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

internal static partial class BrowserLogsPipeBrowserProcessLauncher
{
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

    private const int CREATE_NO_WINDOW = 0x08000000;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint INFINITE = 0xffffffff;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private static readonly IntPtr s_procThreadAttributeHandleList = 0x00020002;
    private const uint WAIT_OBJECT_0 = 0x00000000;

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref UIntPtr lpSize);

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
