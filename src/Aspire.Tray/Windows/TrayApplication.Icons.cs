// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private void UpdateTrayIcon(TrayViewState state)
    {
        var status = TrayIconStatusRules.Get(state);
        if (_iconState == status)
        {
            return;
        }
        _iconData.Icon = _artwork!.TrayIcon(status);
        fixed (char* tip = _iconData.Tip)
        {
            var buffer = new Span<char>(tip, 128);
            buffer.Clear();
            var text = $"Aspire - {TrayIconStatusRules.Describe(status)}";
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

    /// <summary>Owns the notification icons at one DPI.</summary>
    private sealed class Artwork : IDisposable
    {
        private readonly TrayApplication _owner;
        private readonly List<nint> _icons = [];
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
                var light = IsLightTaskbar();
                // The panel's success and warning colors, picked for contrast with the taskbar.
                Running = CreateTrayIcon(original, light ? 0xFF1E9E57 : 0xFF34C46E, dimmed: false);
                Attention = CreateTrayIcon(original, light ? 0xFFD98A00 : 0xFFF2A93B, dimmed: false);
                Dimmed = CreateTrayIcon(original, badge: null, dimmed: true);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal int Size { get; }
        internal nint Original { get; }
        internal nint Running { get; }
        internal nint Attention { get; }
        internal nint Dimmed { get; }

        internal nint TrayIcon(TrayIconStatus status) => status switch
        {
            TrayIconStatus.Running => Running,
            TrayIconStatus.Attention => Attention,
            TrayIconStatus.Idle => Original,
            _ => Dimmed
        };

        private static bool IsLightTaskbar()
        {
            // The taskbar follows the system theme, not the app theme. The value is absent on
            // builds older than the light taskbar, which were dark.
            // https://learn.microsoft.com/windows/apps/desktop/modernize/apply-windows-themes
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        private nint CreateTrayIcon(nint original, uint? badge, bool dimmed)
        {
            using var canvas = new PixelCanvas(_owner, Size);
            canvas.DrawIcon(original);
            // The same rules as macOS: the colored logo alone when idle, a status dot in a
            // transparent cut-out while AppHosts run, and a faded logo while discovery is
            // connecting or unavailable. The cut-out shows the taskbar itself, so the dot
            // stays readable on light, dark and accent-colored taskbars.
            if (badge is uint color)
            {
                var radius = Size * 0.21;
                var center = Size - radius - Size * 0.01;
                canvas.Erase(center, center, radius + Size * 0.07);
                canvas.Circle(center, center, radius, color);
            }
            if (dimmed)
            {
                canvas.Fade(0.45);
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

        /// <summary>Clears an anti-aliased disc to transparent.</summary>
        internal void Erase(double x, double y, double radius)
        {
            for (var row = 0; row < _size; row++)
            {
                for (var column = 0; column < _size; column++)
                {
                    var distance = Math.Sqrt((column + 0.5 - x) * (column + 0.5 - x) + (row + 0.5 - y) * (row + 0.5 - y)) - radius;
                    var coverage = Math.Clamp(0.5 - distance, 0, 1);
                    if (coverage > 0)
                    {
                        Scale(ref Pixels[row * _size + column], 1 - coverage);
                    }
                }
            }
        }

        /// <summary>Multiplies the whole canvas's opacity, keeping premultiplied channels consistent.</summary>
        internal void Fade(double opacity)
        {
            for (var index = 0; index < _size * _size; index++)
            {
                Scale(ref Pixels[index], opacity);
            }
        }

        private static void Scale(ref uint pixel, double factor)
        {
            // Premultiplied BGRA: every channel scales by the same factor.
            uint Channel(uint value, int shift) => (uint)Math.Round(((value >> shift) & 255) * factor) << shift;
            pixel = Channel(pixel, 24) | Channel(pixel, 16) | Channel(pixel, 8) | Channel(pixel, 0);
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
