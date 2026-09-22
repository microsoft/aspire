// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private void UpdateTrayIcon(TrayViewState state)
    {
        var status = GetIconState(state);
        if (_iconState == status)
        {
            return;
        }
        _iconData.Icon = _artwork!.TrayIcon(status);
        fixed (char* tip = _iconData.Tip)
        {
            var buffer = new Span<char>(tip, 128);
            buffer.Clear();
            var text = status switch
            {
                IconState.Active => "Aspire - active AppHosts",
                IconState.Idle => "Aspire - no active AppHosts",
                IconState.Connecting => "Aspire - connecting to discovery",
                _ => "Aspire - discovery unavailable"
            };
            text.AsSpan().CopyTo(buffer);
        }
        if (_iconAdded && NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref _iconData) == 0)
        {
            _iconAdded = false;
            _restoreAttempts = 0;
            Program.Log("The notification icon needs to be restored.");
        }
        _iconState = status;
    }

    private static IconState GetIconState(TrayViewState state) => state.Discovery switch
    {
        DiscoveryState.Connecting => IconState.Connecting,
        DiscoveryState.Live => state.HasActiveAppHosts ? IconState.Active : IconState.Idle,
        _ => IconState.Unavailable
    };

    private enum IconState { Idle, Active, Connecting, Unavailable }
    private enum MenuStatus { Unknown, Healthy, Warning, Unhealthy, Stopped }

    private static MenuStatus GetMenuStatus(AppHostMenuItem? host, bool discoveryAvailable) => host switch
    {
        null => MenuStatus.Unknown,
        { IsStarting: true } or { IsStopping: true } => MenuStatus.Warning,
        _ when !discoveryAvailable => MenuStatus.Warning,
        { Error: not null } => MenuStatus.Unhealthy,
        { IsRunning: false } => MenuStatus.Stopped,
        _ => host.Health switch
        {
            AppHostHealth.Healthy => MenuStatus.Healthy,
            AppHostHealth.Warning => MenuStatus.Warning,
            AppHostHealth.Unhealthy => MenuStatus.Unhealthy,
            _ => MenuStatus.Unknown
        }
    };

    /// <summary>
    /// Owns notification icons and premultiplied health and utility menu bitmaps at one DPI.
    /// </summary>
    private sealed class Artwork : IDisposable
    {
        private readonly TrayApplication _owner;
        private readonly List<nint> _icons = [];
        private readonly List<PixelCanvas> _menuBitmaps = [];
        private readonly Dictionary<MenuStatus, PixelCanvas> _statusBitmaps = [];
        private bool _disposed;

        internal Artwork(TrayApplication owner, uint dpi)
        {
            _owner = owner;
            dpi = dpi == 0 ? 96u : dpi;
            Size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSmallIcon, dpi);
            NativeCallException.Require(Size > 0, "GetSystemMetricsForDpi");
            try
            {
                var original = NativeMethods.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Aspire.ico"),
                    NativeMethods.ImageIcon, Size, Size, NativeMethods.LrLoadFromFile);
                NativeCallException.Require(original != 0, "LoadImageW(Aspire.ico)");
                _icons.Add(original);
                Original = original;
                Connected = CreateTrayIcon(original, IconState.Active);
                Disconnected = CreateTrayIcon(original, IconState.Unavailable);
                Connecting = CreateTrayIcon(original, IconState.Connecting);
                var glyphHeight = Math.Max(1, (int)Math.Round(12d * dpi / 96));
                // Segoe MDL2 Assets: Library (E8F1) and Settings (E713).
                // https://learn.microsoft.com/windows/apps/design/style/segoe-ui-symbol-font
                DocumentationBitmap = CreateMenuBitmap("\uE8F1", glyphHeight);
                SettingsBitmap = CreateMenuBitmap("\uE713", glyphHeight);
                foreach (var status in Enum.GetValues<MenuStatus>())
                {
                    var canvas = new PixelCanvas(owner, Size);
                    _statusBitmaps.Add(status, canvas);
                    var color = status switch
                    {
                        MenuStatus.Healthy => 0xFF269653,
                        MenuStatus.Warning => 0xFFE58A00,
                        MenuStatus.Unhealthy => 0xFFD63E42,
                        _ => 0xFF929292
                    };
                    var radius = Size * 0.3;
                    Action<double, double, double, uint> draw = status == MenuStatus.Stopped ? canvas.Square : canvas.Circle;
                    draw(Size / 2d, Size / 2d, radius, 0xFF606060);
                    draw(Size / 2d, Size / 2d, radius - Size / 16d, color);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal int Size { get; }
        internal nint Original { get; }
        internal nint Connected { get; }
        internal nint Disconnected { get; }
        internal nint Connecting { get; }
        internal nint DocumentationBitmap { get; }
        internal nint SettingsBitmap { get; }
        internal nint Status(MenuStatus status) => _statusBitmaps[status].Handle;

        internal nint TrayIcon(IconState state) => state switch
        {
            IconState.Active => Connected,
            IconState.Connecting => Connecting,
            IconState.Unavailable => Disconnected,
            _ => Original
        };

        private nint CreateMenuBitmap(string glyph, int glyphHeight)
        {
            var canvas = new PixelCanvas(_owner, Size);
            _menuBitmaps.Add(canvas);
            canvas.DrawMenuGlyph(glyph, glyphHeight);
            return canvas.Handle;
        }

        private nint CreateTrayIcon(nint original, IconState state)
        {
            using var canvas = new PixelCanvas(_owner, Size);
            canvas.DrawIcon(original);
            // Preserve the original multicolor Aspire artwork. Only the compact bottom-right
            // status badge is overlaid; a white border contrasts with both taskbar themes.
            var radius = Math.Max(3, Size * 0.22);
            var center = Size - radius;
            canvas.Circle(center, center, radius, 0xFFFFFFFF);
            canvas.Circle(center, center, radius - Size * 0.045,
                state == IconState.Active ? 0xFF7255CC : state == IconState.Connecting ? 0xFF606060 : 0xFF9B6900);
            if (state == IconState.Unavailable)
            {
                canvas.Line(center, center - radius * 0.4, center, center, Size * 0.065, 0xFFFFFFFF);
                canvas.Circle(center, center + radius * 0.4, Size * 0.035, 0xFFFFFFFF);
            }
            else if (state == IconState.Connecting)
            {
                canvas.Line(center - radius * 0.4, center, center + radius * 0.4, center, Size * 0.065, 0xFFFFFFFF);
            }
            // CreateIconIndirect copies both bitmaps. A zero AND mask is used with the
            // 32-bit alpha channel; CreateBitmap(NULL) would leave that mask uninitialized.
            var maskBytes = new byte[((Size + 15) / 16) * 2 * Size];
            fixed (byte* maskBits = maskBytes)
            {
                var mask = NativeMethods.CreateBitmap(Size, Size, 1, 1, (nint)maskBits);
                NativeCallException.Require(mask != 0, "CreateBitmap(icon mask)");
                try
                {
                    var info = new NativeMethods.IconInfo { IsIcon = 1, Color = canvas.Handle, Mask = mask };
                    var icon = NativeMethods.CreateIconIndirect(ref info);
                    NativeCallException.Require(icon != 0, "CreateIconIndirect");
                    _icons.Add(icon);
                    return icon;
                }
                finally
                {
                    _owner.Cleanup(NativeMethods.DeleteObject(mask) != 0, "DeleteObject(icon mask)");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var icon in _icons)
            {
                _owner.Cleanup(NativeMethods.DestroyIcon(icon) != 0, "DestroyIcon");
            }
            // Menus borrow these handles; RefreshMenu destroys them before replacing artwork.
            foreach (var bitmap in _menuBitmaps)
            {
                bitmap.Dispose();
            }
            foreach (var bitmap in _statusBitmaps.Values)
            {
                bitmap.Dispose();
            }
        }
    }

    private sealed class PixelCanvas : IDisposable
    {
        private readonly TrayApplication _owner;
        private readonly int _size;
        internal nint Handle { get; private set; }
        internal uint* Pixels { get; }

        internal PixelCanvas(TrayApplication owner, int size)
        {
            _owner = owner;
            _size = size;
            var info = new NativeMethods.BitmapInfo
            {
                Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32
            };
            Handle = NativeMethods.CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
            NativeCallException.Require(Handle != 0, "CreateDIBSection");
            Pixels = (uint*)bits;
            new Span<uint>(Pixels, size * size).Clear();
        }

        internal void Circle(double x, double y, double radius, uint color)
            => DrawShape((px, py) => Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y)) - radius, color);

        internal void Square(double x, double y, double halfSize, uint color)
            => DrawShape((px, py) => Math.Max(Math.Abs(px - x), Math.Abs(py - y)) - halfSize, color);

        internal void Line(double x1, double y1, double x2, double y2, double width, uint color)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lengthSquared = dx * dx + dy * dy;
            DrawShape((x, y) =>
            {
                var t = Math.Clamp(((x - x1) * dx + (y - y1) * dy) / lengthSquared, 0, 1);
                var px = x - (x1 + t * dx);
                var py = y - (y1 + t * dy);
                return Math.Sqrt(px * px + py * py) - width / 2;
            }, color);
        }

        internal void DrawMenuGlyph(string glyph, int height)
        {
            var dc = NativeMethods.CreateCompatibleDC(0);
            NativeCallException.Require(dc != 0, "CreateCompatibleDC(menu glyph)");
            nint font = 0;
            nint previousFont = 0;
            nint previousBitmap = 0;
            try
            {
                previousBitmap = NativeMethods.SelectObject(dc, Handle);
                NativeCallException.Require(previousBitmap != 0 && previousBitmap != -1, "SelectObject(menu bitmap)");
                // ANTIALIASED_QUALITY produces grayscale coverage, not ClearType's colored
                // fringes. GDI does not supply usable alpha, so draw white on the cleared DIB.
                font = NativeMethods.CreateFont(-height, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe MDL2 Assets");
                NativeCallException.Require(font != 0, "CreateFontW(menu glyph)");
                previousFont = NativeMethods.SelectObject(dc, font);
                NativeCallException.Require(previousFont != 0 && previousFont != -1, "SelectObject(menu font)");
                NativeCallException.Require(NativeMethods.SetTextColor(dc, 0xFFFFFF) != uint.MaxValue, "SetTextColor(menu glyph)");
                NativeCallException.Require(NativeMethods.SetBkMode(dc, 1) != 0, "SetBkMode(menu glyph)"); // TRANSPARENT.
                var bounds = new NativeMethods.Rect { Right = _size, Bottom = _size };
                NativeCallException.Require(NativeMethods.DrawText(dc, glyph, glyph.Length, ref bounds,
                    0x1 | 0x4 | 0x20 | 0x800) > 0, "DrawTextW(menu glyph)"); // CENTER | VCENTER | SINGLELINE | NOPREFIX.
                NativeCallException.Require(NativeMethods.GdiFlush() != 0, "GdiFlush(menu glyph)");

                // COLORREF is 0x00BBGGRR; DIB pixels are premultiplied 0xAARRGGBB.
                // System menu text color also supplies the user's high-contrast foreground.
                var color = NativeMethods.GetSysColor(7); // COLOR_MENUTEXT.
                var red = color & 255;
                var green = (color >> 8) & 255;
                var blue = (color >> 16) & 255;
                for (var index = 0; index < _size * _size; index++)
                {
                    var pixel = Pixels[index];
                    var alpha = ((pixel & 255) + ((pixel >> 8) & 255) + ((pixel >> 16) & 255) + 1) / 3;
                    Pixels[index] = alpha << 24
                        | ((red * alpha + 127) / 255) << 16
                        | ((green * alpha + 127) / 255) << 8
                        | (blue * alpha + 127) / 255;
                }
            }
            finally
            {
                if (previousFont != 0 && previousFont != -1)
                {
                    _owner.Cleanup(NativeMethods.SelectObject(dc, previousFont) != 0, "SelectObject(restore menu font)");
                }
                if (previousBitmap != 0 && previousBitmap != -1)
                {
                    _owner.Cleanup(NativeMethods.SelectObject(dc, previousBitmap) != 0, "SelectObject(restore menu bitmap)");
                }
                _owner.Cleanup(NativeMethods.DeleteDC(dc) != 0, "DeleteDC(menu glyph)");
                if (font != 0)
                {
                    _owner.Cleanup(NativeMethods.DeleteObject(font) != 0, "DeleteObject(menu font)");
                }
            }
        }

        private void DrawShape(Func<double, double, double> distance, uint color)
        {
            // One-pixel coverage ramp and source-over compositing keep edges smooth at
            // fractional DPI sizes. Icon DIBs require premultiplied BGRA, not straight alpha.
            for (var row = 0; row < _size; row++)
            {
                for (var column = 0; column < _size; column++)
                {
                    var coverage = Math.Clamp(0.5 - distance(column + 0.5, row + 0.5), 0, 1);
                    if (coverage > 0)
                    {
                        var alpha = (uint)Math.Round(coverage * (color >> 24));
                        var previous = Pixels[row * _size + column];
                        var inverse = 255 - alpha;
                        uint Channel(int shift) => (((color >> shift) & 255) * alpha
                            + ((previous >> shift) & 255) * inverse + 127) / 255;
                        Pixels[row * _size + column] = (alpha + ((previous >> 24) * inverse + 127) / 255) << 24
                            | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
                    }
                }
            }
        }

        internal void DrawIcon(nint icon)
        {
            var dc = NativeMethods.CreateCompatibleDC(0);
            NativeCallException.Require(dc != 0, "CreateCompatibleDC");
            nint previous = 0;
            try
            {
                previous = NativeMethods.SelectObject(dc, Handle);
                NativeCallException.Require(previous != 0 && previous != -1, "SelectObject");
                NativeCallException.Require(NativeMethods.DrawIconEx(dc, 0, 0, icon, _size, _size, 0, 0, 3) != 0, "DrawIconEx");
                // GDI may batch drawing; synchronize before directly modifying DIB pixels.
                NativeCallException.Require(NativeMethods.GdiFlush() != 0, "GdiFlush");
            }
            finally
            {
                if (previous != 0 && previous != -1)
                {
                    _owner.Cleanup(NativeMethods.SelectObject(dc, previous) != 0, "SelectObject(restore)");
                }
                _owner.Cleanup(NativeMethods.DeleteDC(dc) != 0, "DeleteDC");
            }
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                _owner.Cleanup(NativeMethods.DeleteObject(Handle) != 0, "DeleteObject(canvas)");
                Handle = 0;
            }
        }
    }
}
