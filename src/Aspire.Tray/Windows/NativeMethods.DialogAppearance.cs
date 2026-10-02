// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal static unsafe partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DialogNotification
    {
        public nint Window;
        public nuint Id;
        public int Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DialogCustomDraw
    {
        // NMHDR includes trailing pointer alignment before dwDrawStage.
        public DialogNotification Header;
        public uint Stage;
        public nint Dc;
        public Rect Bounds;
        public nuint Item;
        public uint State;
        public nint Data;
    }

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint window, uint attribute, in uint value, uint size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowTheme(nint window, string? application, string? classes);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint window, int index);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetBkColor(nint dc, uint color);

    [LibraryImport("user32.dll")]
    internal static partial int DrawFocusRect(nint dc, in Rect bounds);
}
