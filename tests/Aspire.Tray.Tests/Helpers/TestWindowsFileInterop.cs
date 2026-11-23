// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray.Tests.Helpers;

[SupportedOSPlatform("windows")]
internal static partial class TestWindowsFileInterop
{
    internal static SafeFileHandle OpenDirectoryForReparseMutation(string path)
        // FSCTL_SET_REPARSE_POINT requires FILE_WRITE_DATA on the directory handle.
        // https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_set_reparse_point
        => CreateFile(path, 0x0002, 0x0007, 0, 3, 0x02200000, 0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
