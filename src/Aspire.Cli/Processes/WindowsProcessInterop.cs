// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Cli.Processes;

/// <summary>
/// Win32 interop that <see cref="System.Diagnostics.Process"/> does not expose.
/// </summary>
internal static partial class WindowsProcessInterop
{
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
}
