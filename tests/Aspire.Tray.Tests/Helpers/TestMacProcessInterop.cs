// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray.Tests.Helpers;

[SupportedOSPlatform("macos")]
internal static partial class TestMacProcessInterop
{
    public static SafeFileHandle DuplicateInheritable(SafeFileHandle handle)
    {
        // dup deliberately clears FD_CLOEXEC, exercising the spawn close-all policy
        // rather than relying on File.OpenHandle's usual close-on-exec default.
        var descriptor = Duplicate(handle);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dup", SetLastError = true)]
    private static partial int Duplicate(SafeFileHandle descriptor);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getsid", SetLastError = true)]
    internal static partial int GetSessionId(int processId);
}
