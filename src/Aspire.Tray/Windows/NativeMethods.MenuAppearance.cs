// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal static unsafe partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuInfo
    {
        public uint Size, Mask, Style, MaxHeight;
        public nint Background;
        public uint ContextHelpId;
        public nuint MenuData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast
    {
        public uint Size, Flags;
        public nint DefaultScheme;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetMenuInfo(nint menu, in MenuInfo info);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    internal static partial int GetHighContrast(uint action, uint parameter, ref HighContrast contrast, uint flags);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    internal static partial int RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint dc, in Rect rect, nint brush);

    [LibraryImport("gdi32.dll")]
    internal static partial int SaveDC(nint dc);

    [LibraryImport("gdi32.dll")]
    internal static partial int RestoreDC(nint dc, int saved);

    [LibraryImport("msimg32.dll")]
    internal static partial int AlphaBlend(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint blend);
}
