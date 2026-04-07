// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Processes;

internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Windows implementation using CreateProcess with STARTUPINFOEX and
    /// PROC_THREAD_ATTRIBUTE_HANDLE_LIST to prevent handle inheritance to grandchildren.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Process StartWindows(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // Open NUL device for stdout/stderr — child writes go nowhere
        using var nulHandle = CreateFileW(
            "NUL",
            GenericWrite,
            FileShareWrite,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero);

        if (nulHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open NUL device");
        }

        // Mark the NUL handle as inheritable (required for STARTUPINFO hStdOutput assignment)
        if (!SetHandleInformation(nulHandle, HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set NUL handle inheritance");
        }

        // Initialize a process thread attribute list with 1 slot (HANDLE_LIST)
        var attrListSize = nint.Zero;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attrListSize);

        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize process thread attribute list");
            }

            try
            {
                // Whitelist only the NUL handle for inheritance.
                // The grandchild (AppHost) will inherit this harmless handle instead of
                // any pipes from the caller's process tree.
                var handles = new[] { nulHandle.DangerousGetHandle() };
                var pinnedHandles = GCHandle.Alloc(handles, GCHandleType.Pinned);
                try
                {
                    if (!UpdateProcThreadAttribute(
                        attrList,
                        0,
                        s_procThreadAttributeHandleList,
                        pinnedHandles.AddrOfPinnedObject(),
                        (nint)(nint.Size * handles.Length),
                        nint.Zero,
                        nint.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update process thread attribute list");
                    }

                    var nulRawHandle = nulHandle.DangerousGetHandle();

                    var si = new STARTUPINFOEX();
                    si.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    si.dwFlags = StartfUseStdHandles;
                    si.hStdInput = nint.Zero;
                    si.hStdOutput = nulRawHandle;
                    si.hStdError = nulRawHandle;
                    si.lpAttributeList = attrList;

                    // Build the command line string: "fileName" arg1 arg2 ...
                    var commandLine = BuildCommandLine(fileName, arguments);

                    var flags = CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow;

                    if (!CreateProcessW(
                        null,
                        commandLine,
                        nint.Zero,
                        nint.Zero,
                        bInheritHandles: true, // TRUE but HANDLE_LIST restricts what's actually inherited
                        flags,
                        nint.Zero,
                        workingDirectory,
                        ref si,
                        out var pi))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create detached process");
                    }

                    Process detachedProcess;
                    try
                    {
                        detachedProcess = Process.GetProcessById(pi.dwProcessId);
                    }
                    finally
                    {
                        // Close the process and thread handles returned by CreateProcess.
                        CloseHandle(pi.hProcess);
                        CloseHandle(pi.hThread);
                    }

                    return detachedProcess;
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
    private static StringBuilder BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();

        // Quote the executable path
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
    private static void AppendArgument(StringBuilder sb, string argument)
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

    // --- Constants ---
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private static readonly nint s_procThreadAttributeHandleList = (nint)0x00020002;

    // --- Structs ---

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // --- P/Invoke declarations ---

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
#pragma warning disable CA1838 // CreateProcessW requires a mutable command line buffer
    private static extern bool CreateProcessW(
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
    private static partial bool CloseHandle(nint hObject);
}
