// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private const int TrayMarkSize = 20;
    private const double StatusIconSize = 12;
    private const uint AspirePurple = 0x512BD4;
    private const uint AvailableColor = 0x4A9F30;
    private const uint AwayColor = 0xDFA638;
    private const uint BusyColor = 0xB52A29;
    private const uint OfflineColor = 0x606060;

    private nint GetTrayImage(bool connected)
    {
        if (_trayImages.TryGetValue(connected, out var existing))
        {
            return existing;
        }
        var image = DrawIcon(22, () =>
        {
            var mark = new AppKit.NativeRect(new(0, 1), new(TrayMarkSize, TrayMarkSize));
            AppKit.DrawImage(_trayMarkImage, AppKit.Selector("drawInRect:fromRect:operation:fraction:"),
                mark, new(new(0, 0), new(0, 0)), 2, 1);
            // Whiten only the mark, preserving its alpha. Making the composite a template
            // would also discard the connection badge's purple color.
            AppKit.SendVoid(GetColor(0xFFFFFF), AppKit.Selector("setFill"));
            AppKit.FillRectUsingOperation(new(new(0, 0), new(22, 22)), 3);
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
            FillCircle(connected ? AspirePurple : 0xFFFFFF, badge);
            if (!connected)
            {
                DrawCross(badge, OfflineColor);
            }
        });
        _trayImages.Add(connected, image);
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
            // lockFocus uses a display-dependent color space. Explicit sRGB bitmap
            // representations preserve the presence palette on both standard and Retina displays.
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
            AppKit.SendBool(image, AppKit.Selector("setTemplate:"), 0);
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
        DrawStroke(rect, [new(0.32, 0.32), new(0.68, 0.68)], color);
        DrawStroke(rect, [new(0.32, 0.68), new(0.68, 0.32)], color);
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

    private nint GetHealthImage(AppHostHealth health)
    {
        if (_healthImages.TryGetValue(health, out var existing))
        {
            return existing;
        }
        var image = DrawIcon(StatusIconSize, () =>
        {
            var rect = new AppKit.NativeRect(new(0, 0), new(StatusIconSize, StatusIconSize));
            FillCircle(health switch
            {
                AppHostHealth.Healthy => AvailableColor,
                AppHostHealth.Warning => AwayColor,
                AppHostHealth.Unhealthy => BusyColor,
                _ => 0xFFFFFF
            }, rect);
        });
        _healthImages.Add(health, image);
        return image;
    }

    private static string HealthDescription(AppHostHealth health) => health switch
    {
        AppHostHealth.Healthy => "All resources healthy",
        AppHostHealth.Warning => "Resources waiting or degraded",
        AppHostHealth.Unhealthy => "Resources failed or unhealthy",
        _ => "Inactive or resource health unavailable"
    };
}
