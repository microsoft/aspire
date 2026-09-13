// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray.Spike;

internal static unsafe partial class NativeMethods
{
    internal const uint WmNull = 0;
    internal const uint WmDestroy = 2;
    internal const uint WmClose = 0x10;
    internal const uint WmEndSession = 0x16;
    internal const uint WmContextMenu = 0x7B;
    internal const uint WmTimer = 0x113;
    internal const uint NinSelect = 0x400;
    internal const uint NinKeySelect = 0x401;
    internal const uint TrayCallback = 0x8001;
    internal const uint NimAdd = 0;
    internal const uint NimDelete = 2;
    internal const uint NimSetVersion = 4;
    internal const uint NotifyIconVersion4 = 4;
    internal const uint NifMessage = 1;
    internal const uint NifIcon = 2;
    internal const uint NifTip = 4;
    internal const uint NifShowTip = 0x80;
    internal const uint MfGrayed = 1;
    internal const uint MfPopup = 0x10;
    internal const uint MfSeparator = 0x800;
    internal const uint TpmRightButton = 2;
    internal const uint TpmNonotify = 0x80;
    internal const uint TpmReturnCmd = 0x100;
    internal const uint ImageIcon = 1;
    internal const uint LrLoadFromFile = 0x10;
    internal const int SmCxSmallIcon = 49;
    internal const int SmCySmallIcon = 50;
    internal const uint MbIconError = 0x10;

    // Default Win32 packing is 8 on both supported architectures (x64 and ARM64).
    // Pointer-sized fields must stay pointer-sized, including WPARAM and menu identifiers.
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        internal uint Style;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal char* MenuName;
        internal char* ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    // Full NOTIFYICONDATAW, including its trailing GUID and balloon icon. Omitting those
    // changes cbSize and can silently select the legacy notification callback protocol.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;
        internal fixed char Tip[128];
        internal uint State;
        internal uint StateMask;
        internal fixed char Info[256];
        internal uint Version;
        internal fixed char InfoTitle[64];
        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIcon;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandle(string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "OutputDebugStringW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial void OutputDebugString(string message);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    internal static partial ushort RegisterClass(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int UnregisterClass(string className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static partial int GetMessage(out Message message, nint window, uint first, uint last);

    [LibraryImport("user32.dll")]
    internal static partial int TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    internal static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint RegisterWindowMessage(string message);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nuint SetTimer(nint window, nuint id, uint interval, nint callback);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int KillTimer(nint window, nuint id);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    internal static partial int ShellNotifyIcon(uint operation, ref NotifyIconData data);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int AppendMenu(nint menu, uint flags, nuint idOrSubmenu, string? text);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    internal static partial int SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll")]
    internal static partial int EndMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetCursorPos(out Point point);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint ShellExecute(nint window, string operation, string file, string? parameters, string? directory, int show);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(nint window, string message, string title, uint type);
}

internal sealed class NativeCallException(string operation, int? error = null)
    : Exception(error is null ? $"{operation} failed." : $"{operation} failed (native error {error}).")
{
    internal static void Require(bool success, string operation)
    {
        if (!success)
        {
            throw new NativeCallException(operation, Marshal.GetLastPInvokeError());
        }
    }
}
