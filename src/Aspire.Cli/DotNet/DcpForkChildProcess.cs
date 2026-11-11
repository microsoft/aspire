// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Hosting.Dcp;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.DotNet;

internal interface IDcpForkChildProcess : IDisposable
{
    int ProcessId { get; }
    int ParentProcessId { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    DcpProcessIdentity GetIdentity();
}

internal sealed partial class DcpForkChildProcess(Process process) : IDcpForkChildProcess
{
    public int ProcessId { get; } = process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;

    public int ParentProcessId
    {
        get
        {
            // Query the same retained child handle used for identity/liveness, not a fresh PID
            // lookup that could belong to an unrelated process after the SDK root exits.
            // PROCESS_BASIC_INFORMATION ABI:
            // https://learn.microsoft.com/windows/win32/procthread/zwqueryinformationprocess
            var size = (uint)Marshal.SizeOf<ProcessBasicInformation>();
            var status = NtQueryInformationProcess(process.SafeHandle, 0, out var information, size, out var returned);
            if (status < 0 || returned != size)
            {
                throw new InvalidOperationException($"Could not inspect DCP fork child parent (NTSTATUS 0x{status:X8}).");
            }
            return checked((int)information.InheritedFromUniqueProcessId);
        }
    }

    public DcpProcessIdentity GetIdentity() => DcpProcessMonitor.GetMonitorProcessIdentity(process);
    public void Dispose() => process.Dispose();

    internal static IDcpForkChildProcess Open(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Retained DCP fork child handles are only needed on Windows.");
        }
        return new DcpForkChildProcess(Process.GetProcessById(pid));
    }

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQueryInformationProcess(
        SafeProcessHandle process, uint informationClass, out ProcessBasicInformation information, uint length, out uint returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public nint PebBaseAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint UniqueProcessId;
        public nuint InheritedFromUniqueProcessId;
    }
}
