// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private void UpdateTrayIcon(TrayViewState state)
    {
        var connected = state.HasActiveAppHosts;
        if (_connectedIcon == connected)
        {
            return;
        }
        _iconData.Icon = connected ? _artwork!.Connected : _artwork!.Disconnected;
        fixed (char* tip = _iconData.Tip)
        {
            var buffer = new Span<char>(tip, 128);
            buffer.Clear();
            (connected ? "Aspire - active AppHosts" : "Aspire - no active AppHosts").AsSpan().CopyTo(buffer);
        }
        if (_iconAdded && NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref _iconData) == 0)
        {
            _iconAdded = false;
            _restoreAttempts = 0;
            Program.Log("The notification icon needs to be restored.");
        }
        _connectedIcon = connected;
    }

    /// <summary>
    /// Owns original-brand tray icons and premultiplied BGRA menu bitmaps at one DPI.
    /// </summary>
    private sealed class Artwork : IDisposable
    {
        private readonly TrayApplication _owner;
        private readonly List<nint> _icons = [];
        private readonly List<nint> _bitmaps = [];
        private readonly Dictionary<AppHostHealth, nint> _health = [];
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
                Connected = CreateTrayIcon(original, true);
                Disconnected = CreateTrayIcon(original, false);
                foreach (var health in Enum.GetValues<AppHostHealth>())
                {
                    using var canvas = new PixelCanvas(owner, Size);
                    var color = health switch
                    {
                        AppHostHealth.Healthy => 0xFF26A85B,
                        AppHostHealth.Warning => 0xFFE5A000,
                        AppHostHealth.Unhealthy => 0xFFD63E42,
                        _ => 0xFFFFFFFF
                    };
                    canvas.Circle(Size / 2d, Size / 2d, Size * 0.32, 0xFF606060);
                    canvas.Circle(Size / 2d, Size / 2d, Size * 0.32 - 1, color);
                    var bitmap = canvas.Detach();
                    _bitmaps.Add(bitmap);
                    _health.Add(health, bitmap);
                }
                using var globe = new PixelCanvas(owner, Size);
                var center = Size / 2d;
                var radius = Size * 0.4;
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var dx = x + 0.5 - center;
                        var dy = y + 0.5 - center;
                        var distance = Math.Sqrt(dx * dx + dy * dy);
                        if (distance <= radius && (distance >= radius - 1.5 || Math.Abs(dx) < 1
                            || Math.Abs(dy) < 1 || Math.Abs(Math.Abs(dy) - radius * 0.5) < 0.6))
                        {
                            globe.Pixels[y * Size + x] = 0xFF7255CC;
                        }
                    }
                }
                Globe = globe.Detach();
                _bitmaps.Add(Globe);
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
        internal nint Globe { get; }
        internal nint Health(AppHostHealth health, bool running) => _health[running ? health : AppHostHealth.Unknown];

        private nint CreateTrayIcon(nint original, bool connected)
        {
            using var canvas = new PixelCanvas(_owner, Size);
            canvas.DrawIcon(original);
            // Preserve the original multicolor Aspire artwork. Only the compact bottom-right
            // status badge is overlaid; a white border contrasts with both taskbar themes.
            var radius = Math.Max(3, Size * 0.22);
            var center = Size - radius;
            canvas.Circle(center, center, radius, 0xFF303030);
            canvas.Circle(center, center, radius - 0.7, 0xFFFFFFFF);
            canvas.Circle(center, center, radius - 1.4, connected ? 0xFF7255CC : 0xFFFFFFFF);
            if (!connected)
            {
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var dx = x + 0.5 - center;
                        var dy = y + 0.5 - center;
                        if (Math.Abs(dx) <= radius * 0.4 && Math.Abs(dy) <= radius * 0.4
                            && (Math.Abs(dx - dy) < 0.7 || Math.Abs(dx + dy) < 0.7))
                        {
                            canvas.Pixels[y * Size + x] = 0xFF404040;
                        }
                    }
                }
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
            foreach (var bitmap in _bitmaps)
            {
                _owner.Cleanup(NativeMethods.DeleteObject(bitmap) != 0, "DeleteObject(menu bitmap)");
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
        {
            for (var row = 0; row < _size; row++)
            {
                for (var column = 0; column < _size; column++)
                {
                    var dx = column + 0.5 - x;
                    var dy = row + 0.5 - y;
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        Pixels[row * _size + column] = color;
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

        internal nint Detach()
        {
            var handle = Handle;
            Handle = 0;
            return handle;
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
