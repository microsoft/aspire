// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private const double TrayCanvasSize = 22;

    // The logo is drawn 17 points wide, centered horizontally, so the visible triangle is about
    // 14 points tall like the system's menu bar glyphs (Wi-Fi, battery). The source triangle ends
    // about 13% above the bottom of its square artwork, so the square sits 1.5 points above the
    // canvas bottom to center the visible triangle vertically.
    private static readonly AppKit.NativeRect s_trayLogoRect = new(new(2.5, 1.5), new(17, 17));

    // The status dot sits in the lower-right corner (non-flipped image coordinates). The
    // logo is cut out 1.5 points around it, so the dot reads on any menu-bar background.
    private static readonly AppKit.NativeRect s_trayBadgeRect = new(new(13.5, 1), new(7, 7));
    private const double TrayBadgeGap = 1.5;

    private nint _trayBadge;

    /// <summary>
    /// Shows the status on the status item: the monochrome logo as a template image, a
    /// colored dot as a separate view (a template image cannot carry color), and the
    /// standard dimmed appearance while discovery is connecting or unavailable.
    /// </summary>
    private void ApplyTrayIcon(TrayIconStatus status)
    {
        var button = AppKit.Get(_statusItem, "button");
        var badge = TrayIconStatusRules.HasBadge(status);
        AppKit.Set(button, "setImage:", GetTrayImage(badge));
        // appearsDisabled is AppKit's own dimmed status-item rendering.
        // https://developer.apple.com/documentation/appkit/nsstatusbarbutton/appearsdisabled
        AppKit.SendBool(button, AppKit.Selector("setAppearsDisabled:"), TrayIconStatusRules.IsDimmed(status) ? (byte)1 : (byte)0);
        if (_trayBadge == 0)
        {
            // A custom NSBox is a filled rounded rectangle whose fill color is resolved at draw
            // time, so the system colors follow the menu bar's light or dark appearance.
            _trayBadge = AppKit.SendRect(AppKit.Get(AppKit.Class("NSBox"), "alloc"), AppKit.Selector("initWithFrame:"), s_trayBadgeRect);
            AppKit.SendInt64(_trayBadge, AppKit.Selector("setBoxType:"), 4); // NSBoxCustom
            AppKit.SendInt64(_trayBadge, AppKit.Selector("setTitlePosition:"), 0); // NSNoTitle
            AppKit.SetDouble(_trayBadge, AppKit.Selector("setBorderWidth:"), 0);
            AppKit.SetDouble(_trayBadge, AppKit.Selector("setCornerRadius:"), s_trayBadgeRect.Size.Width / 2);
            // The status item's accessibility label already states the status.
            AppKit.SendBool(_trayBadge, AppKit.Selector("setAccessibilityElement:"), 0);
            AppKit.Set(button, "addSubview:", _trayBadge);
        }
        AppKit.SendBool(_trayBadge, AppKit.Selector("setHidden:"), badge ? (byte)0 : (byte)1);
        if (!badge)
        {
            return;
        }
        AppKit.Set(_trayBadge, "setFillColor:", AppKit.Get(AppKit.Class("NSColor"),
            status == TrayIconStatus.Running ? "systemGreenColor" : "systemOrangeColor"));
        // NSButton centers an image-only cell; place the dot over the image's badge cut-out.
        var bounds = AppKit.GetRect(button, "bounds");
        var x = (bounds.Size.Width - TrayCanvasSize) / 2 + s_trayBadgeRect.Origin.X;
        var imageY = (bounds.Size.Height - TrayCanvasSize) / 2;
        var y = AppKit.GetBool(button, AppKit.Selector("isFlipped")) != 0
            ? imageY + TrayCanvasSize - s_trayBadgeRect.Origin.Y - s_trayBadgeRect.Size.Height
            : imageY + s_trayBadgeRect.Origin.Y;
        AppKit.SetRect(_trayBadge, AppKit.Selector("setFrame:"), new(new(Math.Round(x * 2) / 2, Math.Round(y * 2) / 2), s_trayBadgeRect.Size));
    }

    private void RemoveTrayBadge()
    {
        if (_trayBadge != 0)
        {
            AppKit.SendVoid(_trayBadge, AppKit.Selector("removeFromSuperview"));
            AppKit.Release(_trayBadge);
            _trayBadge = 0;
        }
    }

    private nint GetTrayImage(bool badge)
    {
        if (_trayImages.TryGetValue(badge, out var existing))
        {
            return existing;
        }
        var image = DrawIcon(TrayCanvasSize, () =>
        {
            AppKit.DrawImage(_trayMarkImage, AppKit.Selector("drawInRect:fromRect:operation:fraction:"),
                s_trayLogoRect, new(new(0, 0), new(0, 0)), 2, 1);
            if (!badge)
            {
                return;
            }
            // Erase the logo under and around the dot rather than painting an outline, so
            // the gap shows whatever the menu bar is (material, wallpaper tint, highlight).
            AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("saveGraphicsState"));
            try
            {
                var cutout = new AppKit.NativeRect(
                    new(s_trayBadgeRect.Origin.X - TrayBadgeGap, s_trayBadgeRect.Origin.Y - TrayBadgeGap),
                    new(s_trayBadgeRect.Size.Width + 2 * TrayBadgeGap, s_trayBadgeRect.Size.Height + 2 * TrayBadgeGap));
                var clip = AppKit.SendRect(AppKit.Class("NSBezierPath"), AppKit.Selector("bezierPathWithOvalInRect:"), cutout);
                AppKit.SendVoid(clip, AppKit.Selector("addClip"));
                AppKit.FillRectUsingOperation(cutout, 0);
            }
            finally
            {
                AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("restoreGraphicsState"));
            }
        });
        _trayImages.Add(badge, image);
        return image;
    }

    /// <summary>
    /// Builds the menu-bar logo from the same colored Aspire artwork Windows uses, as a
    /// one-color template image (Apple's guidance for menu bar extras). Darker parts of the
    /// artwork (the outline) become opaque and lighter facets become partly transparent, so
    /// the logo keeps its inner structure in a single color. The conversion maps sRGB
    /// luminance L to alpha as clamp(4/3 * (1 - L)): the #512BD4 outline (L about 0.25)
    /// stays opaque and the lightest facet (L about 0.85) keeps about 20% coverage.
    /// https://developer.apple.com/design/human-interface-guidelines/the-menu-bar#Menu-bar-extras
    /// </summary>
    private unsafe nint LoadTrayMark()
    {
        const int Pixels = 256;
        var allocated = AppKit.CreateBitmap(AppKit.Get(AppKit.Class("NSBitmapImageRep"), "alloc"),
            AppKit.Selector("initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:samplesPerPixel:hasAlpha:isPlanar:colorSpaceName:bytesPerRow:bitsPerPixel:"),
            0, Pixels, Pixels, 8, 4, 1, 0, AppKit.Constant("NSDeviceRGBColorSpace"), 0, 0);
        if (allocated == 0)
        {
            throw new InvalidOperationException("Could not allocate the tray logo.");
        }
        nint bitmap;
        try
        {
            bitmap = AppKit.Get(allocated, "bitmapImageRepByRetaggingWithColorSpace:", AppKit.Get(AppKit.Class("NSColorSpace"), "sRGBColorSpace"));
            if (bitmap == 0)
            {
                throw new InvalidOperationException("Could not select the tray logo color space.");
            }
            AppKit.Get(bitmap, "retain");
        }
        finally
        {
            AppKit.Release(allocated);
        }
        nint image = 0;
        try
        {
            var context = AppKit.Get(AppKit.Class("NSGraphicsContext"), "graphicsContextWithBitmapImageRep:", bitmap);
            if (context == 0)
            {
                throw new InvalidOperationException("Could not draw the tray logo.");
            }
            AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("saveGraphicsState"));
            try
            {
                AppKit.Set(AppKit.Class("NSGraphicsContext"), "setCurrentContext:", context);
                AppKit.FillRectUsingOperation(new(new(0, 0), new(Pixels, Pixels)), 0);
                AppKit.DrawImage(_brandImage, AppKit.Selector("drawInRect:fromRect:operation:fraction:"),
                    new(new(0, 0), new(Pixels, Pixels)), new(new(0, 0), new(0, 0)), 2, 1);
                AppKit.SendVoid(context, AppKit.Selector("flushGraphics"));
            }
            finally
            {
                AppKit.SendVoid(AppKit.Class("NSGraphicsContext"), AppKit.Selector("restoreGraphicsState"));
            }

            // The default NSBitmapImageRep layout is 8-bit RGBA with premultiplied alpha.
            var data = (byte*)AppKit.Get(bitmap, "bitmapData");
            var stride = (int)AppKit.Get(bitmap, "bytesPerRow");
            for (var y = 0; y < Pixels; y++)
            {
                var row = data + y * stride;
                for (var x = 0; x < Pixels; x++)
                {
                    var pixel = row + x * 4;
                    var alpha = pixel[3];
                    if (alpha == 0)
                    {
                        continue;
                    }
                    var luminance = (0.2126 * pixel[0] + 0.7152 * pixel[1] + 0.0722 * pixel[2]) / alpha;
                    var coverage = Math.Clamp(4.0 / 3.0 * (1 - luminance), 0, 1);
                    // Premultiplied black: only the alpha channel carries the template shape.
                    pixel[0] = pixel[1] = pixel[2] = 0;
                    pixel[3] = (byte)Math.Round(alpha * coverage);
                }
            }

            image = AppKit.SendSizeReturningPointer(AppKit.Get(AppKit.Class("NSImage"), "alloc"),
                AppKit.Selector("initWithSize:"), s_trayLogoRect.Size);
            if (image == 0)
            {
                throw new InvalidOperationException("Could not create the tray logo.");
            }
            AppKit.SendSize(bitmap, AppKit.Selector("setSize:"), s_trayLogoRect.Size);
            AppKit.Set(image, "addRepresentation:", bitmap);
            AppKit.SendBool(image, AppKit.Selector("setTemplate:"), 1);
            return image;
        }
        catch
        {
            AppKit.Release(image);
            throw;
        }
        finally
        {
            AppKit.Release(bitmap);
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
                        AppKit.SendInt64(context, AppKit.Selector("setImageInterpolation:"), 3); // NSImageInterpolationHigh
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
}
