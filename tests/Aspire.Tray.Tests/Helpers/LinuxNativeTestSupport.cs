// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray.Tests.Helpers;

internal static class LinuxNativeTestSupport
{
    internal static bool IsAvailable(string library)
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(library, out var handle))
        {
            return false;
        }
        NativeLibrary.Free(handle);
        return true;
    }
}
