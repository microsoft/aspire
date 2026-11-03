// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private void CopyPath(string path)
    {
        if (smokeSeconds is not null && !_interactiveSmoke)
        {
            (CopyPathForSmoke ?? throw new InvalidOperationException("Smoke clipboard handler is missing."))(path);
            return;
        }
        // CF_UNICODETEXT needs a movable, NUL-terminated buffer. Prepare it before
        // emptying the clipboard; ownership transfers only after SetClipboardData succeeds.
        // https://learn.microsoft.com/windows/win32/dataxchg/using-the-clipboard
        var memory = NativeMethods.GlobalAlloc(0x2, checked((nuint)(path.Length + 1) * sizeof(char)));
        NativeCallException.Require(memory != 0, "GlobalAlloc(clipboard)");
        try
        {
            var buffer = NativeMethods.GlobalLock(memory);
            NativeCallException.Require(buffer != 0, "GlobalLock(clipboard)");
            path.AsSpan().CopyTo(new Span<char>((void*)buffer, path.Length));
            ((char*)buffer)[path.Length] = '\0';
            // Returning zero is also success when the last lock is released.
            Marshal.SetLastPInvokeError(0);
            var unlocked = NativeMethods.GlobalUnlock(memory);
            NativeCallException.Require(unlocked != 0 || Marshal.GetLastPInvokeError() == 0, "GlobalUnlock(clipboard)");
            NativeCallException.Require(NativeMethods.OpenClipboard(_window) != 0, "OpenClipboard");
            try
            {
                NativeCallException.Require(NativeMethods.EmptyClipboard() != 0, "EmptyClipboard");
                NativeCallException.Require(NativeMethods.SetClipboardData(13, memory) != 0, "SetClipboardData");
                memory = 0;
            }
            finally
            {
                NativeCallException.Require(NativeMethods.CloseClipboard() != 0, "CloseClipboard");
            }
        }
        finally
        {
            if (memory != 0)
            {
                Cleanup(NativeMethods.GlobalFree(memory) == 0, "GlobalFree(clipboard)");
            }
        }
    }
}
