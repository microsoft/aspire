// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>
/// Win32, DWM and GDI+ flat API declarations used only by the flyout panel. GDI+ ships with
/// every supported Windows version (gdiplus.dll), so it adds no dependency.
/// https://learn.microsoft.com/windows/win32/gdiplus/-gdiplus-flatapi-flat
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const uint WmActivate = 0x0006;
    internal const uint WmPaint = 0x000F;
    internal const uint WmEraseBackground = 0x0014;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLeftButtonDown = 0x0201;
    internal const uint WmLeftButtonUp = 0x0202;
    internal const uint WmMouseLeave = 0x02A3;
    internal const uint WmCaptureChanged = 0x0215;
    internal const uint WsPopup = 0x80000000;
    internal const uint WsExTopmost = 0x00000008;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint CsDropShadow = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        public nint Dc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncrementalUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public nint Window;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid ItemGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointF
    {
        public float X;
        public float Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    internal static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint BeginPaint(nint window, out PaintStruct paint);

    [LibraryImport("user32.dll")]
    internal static partial int EndPaint(nint window, in PaintStruct paint);

    [LibraryImport("user32.dll")]
    internal static partial int InvalidateRect(nint window, nint rect, int erase);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int TrackMouseEvent(ref TrackMouseEventData data);

    [LibraryImport("user32.dll")]
    internal static partial nint SetCapture(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseCapture();

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowRgn(nint window, nint region, int redraw);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    internal static partial int GetHighContrast(uint action, uint size, ref HighContrast value, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    internal static partial int ShellNotifyIconGetRect(in NotifyIconIdentifier identifier, out Rect rect);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint window, uint attribute, void* value, uint size);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    internal static partial int SaveDC(nint dc);

    [LibraryImport("gdi32.dll")]
    internal static partial int RestoreDC(nint dc, int saved);

    [LibraryImport("gdi32.dll")]
    internal static partial int IntersectClipRect(nint dc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextFaceW")]
    internal static partial int GetTextFace(nint dc, int count, char* face);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdiplusStartup(out nuint token, in GdiplusStartupInput input, nint output);

    [LibraryImport("gdiplus.dll")]
    internal static partial void GdiplusShutdown(nuint token);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateFromHDC(nint dc, out nint graphics);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetSmoothingMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipScaleWorldTransform(nint graphics, float x, float y, int order);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreatePath2(PointF* points, byte* types, int count, int fillMode, out nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeletePath(nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateSolidFill(uint argb, out nint brush);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeleteBrush(nint brush);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipFillPath(nint graphics, nint brush, nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeletePen(nint pen);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetPenLineCap197819(nint pen, int startCap, int endCap, int dashCap);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetPenLineJoin(nint pen, int join);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDrawPath(nint graphics, nint pen, nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetClipRect(nint graphics, float x, float y, float width, float height, int combineMode);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSaveGraphics(nint graphics, out uint state);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipRestoreGraphics(nint graphics, uint state);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipGetDC(nint graphics, out nint dc);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipReleaseDC(nint graphics, nint dc);

    [LibraryImport("user32.dll")]
    internal static partial int GetKeyboardState(byte* state);

    [LibraryImport("user32.dll")]
    internal static partial int SetKeyboardState(byte* state);

    // Tooltip control messages and flags (commctrl.h), for the panel's icon buttons.
    // https://learn.microsoft.com/windows/win32/controls/tooltip-control-reference
    internal const uint TtmAddTool = 0x0432; // TTM_ADDTOOLW
    internal const uint TtmDelTool = 0x0433; // TTM_DELTOOLW
    internal const uint TtfSubclass = 0x10;
    internal const uint TtsAlwaysTip = 0x1;
    internal const uint TtsNoPrefix = 0x2;
    internal const uint IccBarClasses = 0x4; // Registers TOOLTIPS_CLASS.

    [StructLayout(LayoutKind.Sequential)]
    internal struct CommonControls
    {
        public uint Size;
        public uint Classes;
    }

    // TTTOOLINFOW with the Windows XP+ lpReserved field.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ToolInfo
    {
        public uint Size;
        public uint Flags;
        public nint Window;
        public nuint Id;
        public Rect Rect;
        public nint Instance;
        public char* Text;
        public nint Parameter;
        public nint Reserved;
    }

    [LibraryImport("comctl32.dll", SetLastError = true)]
    internal static partial int InitCommonControlsEx(in CommonControls controls);

    // Extracts an executable's icon at an exact pixel size: LOWORD of size is the large icon's.
    // https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shdefextracticonw
    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHDefExtractIcon(string iconFile, int index, uint flags, out nint large, nint* small, uint size);

    [LibraryImport("user32.dll")]
    internal static partial int UpdateWindow(nint window);

    // Smoke preview screenshots: copy the composited screen under the panel and save it as PNG.
    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateBitmapFromHBITMAP(nint bitmap, nint palette, out nint image);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GdipSaveImageToFile(nint image, string fileName, in Guid encoder, nint parameters);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDisposeImage(nint image);

    // TEXTMETRICW: the panel only reads the ascent and height, to place a baseline.
    // https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-textmetricw
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextMetric
    {
        public int Height;
        public int Ascent;
        public int Descent;
        public int InternalLeading;
        public int ExternalLeading;
        public int AverageCharWidth;
        public int MaxCharWidth;
        public int Weight;
        public int Overhang;
        public int DigitizedAspectX;
        public int DigitizedAspectY;
        public ushort FirstChar;
        public ushort LastChar;
        public ushort DefaultChar;
        public ushort BreakChar;
        public byte Italic;
        public byte Underlined;
        public byte StruckOut;
        public byte PitchAndFamily;
        public byte CharSet;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextMetricsW")]
    internal static partial int GetTextMetrics(nint dc, out TextMetric metrics);
}
