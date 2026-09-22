// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray.Tests.Helpers;

[SupportedOSPlatform("windows")]
internal sealed partial class TestWindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public static bool HasDesktopShell => GetShellWindow() != 0;

    public unsafe TestWindowsProcessJob(uint limitFlags)
    {
        _handle = CreateJobObject(0, null);
        try
        {
            if (_handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            var limits = new ExtendedLimitInformation
            {
                BasicLimits = new BasicLimitInformation { Flags = limitFlags }
            };
            if (!SetInformationJobObject(_handle, 9, &limits, (uint)sizeof(ExtendedLimitInformation)))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public void Assign(SafeProcessHandle process)
    {
        if (!AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public bool Contains(SafeProcessHandle process)
    {
        if (!IsProcessInJob(process, _handle, out var result))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return result;
    }

    public static int GetProcessId(SafeProcessHandle process)
    {
        var id = GetNativeProcessId(process);
        if (id == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return checked((int)id);
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static partial uint GetNativeProcessId(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTimeLimit;
        public long JobTimeLimit;
        public uint Flags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimits;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }
}
