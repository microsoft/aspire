// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private const int TrayMarkSize = 20;
    private const double StatusIconSize = 14;

    private static TrayIconState GetTrayIconState(TrayViewState state) => state.Discovery switch
    {
        DiscoveryState.Connecting => TrayIconState.Connecting,
        DiscoveryState.Live => state.HasActiveAppHosts ? TrayIconState.Active : TrayIconState.Idle,
        _ => TrayIconState.Disconnected
    };

    private static string TrayIconDescription(TrayIconState state) => state switch
    {
        TrayIconState.Active => "AppHosts running",
        TrayIconState.Idle => "No AppHosts running",
        TrayIconState.Connecting => "Connecting to discovery",
        _ => "Discovery unavailable"
    };

    private nint GetTrayImage(TrayIconState state)
    {
        if (_trayImages.TryGetValue(state, out var existing))
        {
            return existing;
        }
        var image = DrawIcon(22, () =>
        {
            var mark = new AppKit.NativeRect(new(0, 1), new(TrayMarkSize, TrayMarkSize));
            AppKit.DrawImage(_trayMarkImage, AppKit.Selector("drawInRect:fromRect:operation:fraction:"),
                mark, new(new(0, 0), new(0, 0)), 2, 1);
            // Preserve the supplied brand silhouette. The entire composite is a template:
            // AppKit chooses its contrast for the menu-bar material and selected appearance.
            // https://developer.apple.com/documentation/appkit/nsimage/istemplate
            if (state == TrayIconState.Idle)
            {
                return;
            }
            // Erase the mark under the badge rather than painting a background-colored
            // outline. The transparent ring follows any menu-bar material or wallpaper.
            AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("saveGraphicsState"));
            try
            {
                var cutout = new AppKit.NativeRect(new(12, -1), new(11, 11));
                var clip = AppKit.SendRect(AppKit.Class("NSBezierPath"), AppKit.Selector("bezierPathWithOvalInRect:"), cutout);
                AppKit.SendVoid(clip, AppKit.Selector("addClip"));
                AppKit.FillRectUsingOperation(cutout, 0);
            }
            finally
            {
                AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("restoreGraphicsState"));
            }
            var badge = new AppKit.NativeRect(new(13, 0), new(9, 9));
            switch (state)
            {
                case TrayIconState.Active:
                    FillCircle(0, badge);
                    break;
                case TrayIconState.Disconnected:
                    DrawCross(badge, 0);
                    break;
                case TrayIconState.Connecting:
                    foreach (var x in new[] { 13.0, 16.5, 20.0 })
                    {
                        FillCircle(0, new(new(x, 3.5), new(2, 2)));
                    }
                    break;
            }
        });
        _trayImages.Add(state, image);
        return image;
    }

    private static nint LoadTrayMark()
    {
        var image = AppKit.SendSizeReturningPointer(AppKit.Get(AppKit.Class("NSImage"), "alloc"),
            AppKit.Selector("initWithSize:"), new(TrayMarkSize, TrayMarkSize));
        if (image == 0)
        {
            throw new InvalidOperationException("Could not create the tray template image.");
        }
        try
        {
            // Resource streams have no filename-based @2x discovery. Register both
            // supplied rasters at the same logical size so AppKit selects the native scale.
            foreach (var scale in new[] { 2, 1 })
            {
                var resourceName = scale == 2 ? "AspireTrayTemplate@2x.png" : "AspireTrayTemplate.png";
                var source = LoadEmbeddedImage(resourceName);
                try
                {
                    var representations = AppKit.Get(source, "representations");
                    if (AppKit.Get(representations, "count") != 1)
                    {
                        throw new InvalidOperationException($"Expected one bitmap in '{resourceName}'.");
                    }
                    var bitmap = AppKit.Get(representations, "objectAtIndex:", 0);
                    if (AppKit.Get(bitmap, "pixelsWide") != TrayMarkSize * scale || AppKit.Get(bitmap, "pixelsHigh") != TrayMarkSize * scale)
                    {
                        throw new InvalidOperationException($"Unexpected dimensions for '{resourceName}'.");
                    }
                    AppKit.SendSize(bitmap, AppKit.Selector("setSize:"), new(TrayMarkSize, TrayMarkSize));
                    AppKit.Set(image, "addRepresentation:", bitmap);
                }
                finally
                {
                    AppKit.Release(source);
                }
            }
            AppKit.SendBool(image, AppKit.Selector("setTemplate:"), 1);
            return image;
        }
        catch
        {
            AppKit.Release(image);
            throw;
        }
    }

    private static nint DrawIcon(double size, Action draw)
    {
        var image = AppKit.SendSizeReturningPointer(AppKit.Get(AppKit.Class("NSImage"), "alloc"),
            AppKit.Selector("initWithSize:"), new(size, size));
        if (image == 0)
        {
            throw new InvalidOperationException("Could not create a tray status icon.");
        }
        try
        {
            // Explicit 1x/2x bitmaps avoid lockFocus's display-dependent raster scale.
            foreach (var scale in new[] { 2, 1 })
            {
                var pixels = (nint)(size * scale);
                var allocated = AppKit.CreateBitmap(AppKit.Get(AppKit.Class("NSBitmapImageRep"), "alloc"),
                    AppKit.Selector("initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:samplesPerPixel:hasAlpha:isPlanar:colorSpaceName:bytesPerRow:bitsPerPixel:"),
                    0, pixels, pixels, 8, 4, 1, 0, AppKit.Constant("NSDeviceRGBColorSpace"), 0, 0);
                if (allocated == 0)
                {
                    throw new InvalidOperationException("Could not allocate tray artwork.");
                }
                nint bitmap;
                try
                {
                    bitmap = AppKit.Get(allocated, "bitmapImageRepByRetaggingWithColorSpace:",
                        AppKit.Get(AppKit.Class("NSColorSpace"), "sRGBColorSpace"));
                    if (bitmap == 0)
                    {
                        throw new InvalidOperationException("Could not select the tray artwork color space.");
                    }
                    AppKit.Get(bitmap, "retain");
                }
                finally
                {
                    AppKit.Release(allocated);
                }
                try
                {
                    var context = AppKit.Get(AppKit.Class("NSGraphicsContext"), "graphicsContextWithBitmapImageRep:", bitmap);
                    if (context == 0)
                    {
                        throw new InvalidOperationException("Could not draw tray artwork.");
                    }
                    AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("saveGraphicsState"));
                    try
                    {
                        AppKit.Set(AppKit.Class("NSGraphicsContext"), "setCurrentContext:", context);
                        AppKit.SendBool(context, AppKit.Selector("setShouldAntialias:"), 1);
                        AppKit.FillRectUsingOperation(new(new(0, 0), new(pixels, pixels)), 0);
                        var transform = AppKit.Get(AppKit.Class("NSAffineTransform"), "transform");
                        AppKit.SetDouble(transform, AppKit.Selector("scaleBy:"), scale);
                        AppKit.SendVoid(transform, AppKit.Selector("concat"));
                        draw();
                    }
                    finally
                    {
                        AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("restoreGraphicsState"));
                    }
                    AppKit.SendSize(bitmap, AppKit.Selector("setSize:"), new(size, size));
                    AppKit.Set(image, "addRepresentation:", bitmap);
                }
                finally
                {
                    AppKit.Release(bitmap);
                }
            }
            AppKit.SendBool(image, AppKit.Selector("setTemplate:"), 1);
            return image;
        }
        catch
        {
            AppKit.Release(image);
            throw;
        }
    }

    private static void FillCircle(uint color, AppKit.NativeRect rect)
    {
        AppKit.SendVoid(GetColor(color), AppKit.Selector("setFill"));
        var path = AppKit.SendRect(AppKit.Class("NSBezierPath"), AppKit.Selector("bezierPathWithOvalInRect:"), rect);
        AppKit.SendVoid(path, AppKit.Selector("fill"));
    }

    private static nint GetColor(uint color)
        => AppKit.SendFourDoubles(AppKit.Class("NSColor"), AppKit.Selector("colorWithSRGBRed:green:blue:alpha:"),
            (color >> 16 & 0xff) / 255.0, (color >> 8 & 0xff) / 255.0, (color & 0xff) / 255.0, 1);

    private static void DrawCross(AppKit.NativeRect rect, uint color)
    {
        DrawStroke(rect, [new(0.2, 0.2), new(0.8, 0.8)], color);
        DrawStroke(rect, [new(0.2, 0.8), new(0.8, 0.2)], color);
    }

    private static void DrawStroke(AppKit.NativeRect rect, AppKit.NativePoint[] points, uint color)
    {
        var path = AppKit.Get(AppKit.Class("NSBezierPath"), "bezierPath");
        AppKit.SetDouble(path, AppKit.Selector("setLineWidth:"), rect.Size.Width * 0.12);
        AppKit.Set(path, "setLineCapStyle:", 1);
        AppKit.Set(path, "setLineJoinStyle:", 1);
        for (var i = 0; i < points.Length; i++)
        {
            var point = new AppKit.NativePoint(rect.Origin.X + points[i].X * rect.Size.Width,
                rect.Origin.Y + points[i].Y * rect.Size.Height);
            AppKit.SendPoint(path, AppKit.Selector(i == 0 ? "moveToPoint:" : "lineToPoint:"), point);
        }
        AppKit.SendVoid(GetColor(color), AppKit.Selector("setStroke"));
        AppKit.SendVoid(path, AppKit.Selector("stroke"));
    }

    private nint GetHealthImage(AppHostHealth health, bool running)
    {
        var key = (running ? health : AppHostHealth.Unknown, running);
        if (_healthImages.TryGetValue(key, out var existing))
        {
            return existing;
        }
        // Native symbols remain legible in light/dark/selected menus and carry meaning
        // without relying on color perception. An inactive host is neutral, not an error.
        var symbol = AppKit.SendTwoPointers(AppKit.Class("NSImage"),
            AppKit.Selector("imageWithSystemSymbolName:accessibilityDescription:"),
            AppKit.String(HealthSymbol(health, running)), AppKit.String(HealthDescription(health, running)));
        if (symbol == 0)
        {
            throw new InvalidOperationException("A required AppHost health symbol is unavailable.");
        }
        var image = AppKit.Get(symbol, "copy");
        AppKit.SendSize(image, AppKit.Selector("setSize:"), new(StatusIconSize, StatusIconSize));
        AppKit.SendBool(image, AppKit.Selector("setTemplate:"), 1);
        _healthImages.Add(key, image);
        return image;
    }

    private static string HealthSymbol(AppHostHealth health, bool running) => !running ? "stop.circle" : health switch
    {
        AppHostHealth.Healthy => "checkmark.circle",
        AppHostHealth.Warning => "exclamationmark.triangle",
        AppHostHealth.Unhealthy => "xmark.octagon",
        _ => "questionmark.circle"
    };

    private static string HealthDescription(AppHostHealth health, bool running) => !running ? "AppHost stopped" : health switch
    {
        AppHostHealth.Healthy => "All resources healthy",
        AppHostHealth.Warning => "Resources waiting or degraded",
        AppHostHealth.Unhealthy => "Resources failed or unhealthy",
        _ => "Resource health unavailable"
    };

    private enum TrayIconState
    {
        Idle,
        Active,
        Connecting,
        Disconnected
    }
}
