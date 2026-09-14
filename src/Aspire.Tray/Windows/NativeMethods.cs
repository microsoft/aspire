// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

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
    // Private window messages are shared with the activation dispatcher, never thread messages.
    internal const uint ReadyMessage = 0x8002;
    internal const uint RefreshMessage = 0x8003;
    internal const uint RestoreMessage = 0x8004;
    internal const uint QuitMessage = 0x8005;
    internal const uint SmokeMessage = 0x8006;
    internal const uint WmDpiChanged = 0x02E0;
    internal const uint WmSettingChange = 0x001A;
    internal const uint WmMenuRightButtonUp = 0x0122;
    internal const uint NimAdd = 0;
    internal const uint NimModify = 1;
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
    internal const uint SafeConfirmation = 0x00000001 | 0x00000030 | 0x00000100; // OK/Cancel, warning, Cancel default.
    internal const uint MiimState = 0x1;
    internal const uint MiimString = 0x40;
    internal const uint MiimBitmap = 0x80;
    internal const uint MiimId = 0x2;
    internal const uint MiimSubmenu = 0x4;
    internal const uint MfByPosition = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint Submenu;
        public nint CheckedBitmap;
        public nint UncheckedBitmap;
        public nuint ItemData;
        public char* Text;
        public uint TextLength;
        public nint Bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        public int IsIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint Mask;
        public nint Color;
    }

    // Default Win32 packing is 8 on both supported architectures (x64 and ARM64).
    // Pointer-sized fields must stay pointer-sized, including WPARAM and menu identifiers.
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    // Full NOTIFYICONDATAW, including its trailing GUID and balloon icon. Omitting those
    // changes cbSize and can silently select the legacy notification callback protocol.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
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

    [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW", SetLastError = true)]
    internal static partial int SetMenuItemInfo(nint menu, uint item, int byPosition, ref MenuItemInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW", SetLastError = true)]
    internal static partial int GetMenuItemInfo(nint menu, uint item, int byPosition, ref MenuItemInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetMenuItemCount(nint menu);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint SelectObject(nint dc, nint value);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int DeleteDC(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int DeleteObject(nint value);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, nint bits);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("gdi32.dll")]
    internal static partial int GdiFlush();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial nint GetLastActivePopup(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int EnumThreadWindows(uint threadId, delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    internal static partial int GetClassName(nint window, char* className, int capacity);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int RedrawWindow(nint window, nint updateRect, nint updateRegion, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ParseDisplayName(string name, nint bindingContext, out nint itemIdList, uint attributes, out uint outputAttributes);

    [LibraryImport("shell32.dll", EntryPoint = "SHOpenFolderAndSelectItems")]
    internal static partial int OpenFolderAndSelectItems(nint itemIdList, uint count, nint items, uint flags);
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
