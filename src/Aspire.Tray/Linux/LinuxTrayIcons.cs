// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// Keeps status artwork alive while a desktop panel may still be reading it.
/// </summary>
internal sealed class LinuxTrayIcons(string source) : IDisposable
{
    private readonly Dictionary<TrayIconStatus, string> _paths = [];
    private DirectoryInfo? _directory;

    internal string GetPath(TrayIconStatus status)
    {
        if (_paths.TryGetValue(status, out var path))
        {
            return path;
        }
        if (status == TrayIconStatus.Idle)
        {
            return source;
        }
        _directory ??= Directory.CreateTempSubdirectory("aspire-tray-icons-");
        path = Path.Combine(_directory.FullName, $"{status}.png");
        Render(source, path, status);
        _paths.Add(status, path);
        return path;
    }

    internal static void Render(string source, string destination, TrayIconStatus status)
    {
        var logo = Gtk.cairo_image_surface_create_from_png(source);
        nint surface = 0;
        nint context = 0;
        try
        {
            if (Gtk.cairo_surface_status(logo) != 0)
            {
                throw new InvalidOperationException("Could not read the Aspire tray icon.");
            }
            var width = Gtk.cairo_image_surface_get_width(logo);
            var height = Gtk.cairo_image_surface_get_height(logo);
            surface = Gtk.cairo_image_surface_create(0, width, height);
            context = Gtk.cairo_create(surface);
            Gtk.cairo_set_source_surface(context, logo, 0, 0);
            Gtk.cairo_paint_with_alpha(context, TrayIconStatusRules.IsDimmed(status) ? 0.4 : 1);
            if (TrayIconStatusRules.HasBadge(status))
            {
                var radius = Math.Min(width, height) * 0.16;
                var x = width - radius * 1.15;
                var y = height - radius * 1.15;
                // Clear a gap around the badge so it contrasts on any panel theme.
                // https://www.cairographics.org/operators/
                Gtk.cairo_set_operator(context, 0); // CAIRO_OPERATOR_CLEAR
                Gtk.cairo_arc(context, x, y, radius * 1.25, 0, Math.Tau);
                Gtk.cairo_fill(context);
                Gtk.cairo_set_operator(context, 2); // CAIRO_OPERATOR_OVER
                var color = status == TrayIconStatus.Running ? 0x269653 : 0xE58A00;
                Gtk.cairo_set_source_rgb(context, ((color >> 16) & 255) / 255d, ((color >> 8) & 255) / 255d, (color & 255) / 255d);
                Gtk.cairo_arc(context, x, y, radius, 0, Math.Tau);
                Gtk.cairo_fill(context);
            }
            if (Gtk.cairo_status(context) != 0 || Gtk.cairo_surface_status(surface) != 0
                || Gtk.cairo_surface_write_to_png(surface, destination) != 0)
            {
                throw new InvalidOperationException("Could not render the Aspire tray status icon.");
            }
        }
        finally
        {
            if (context != 0)
            {
                Gtk.cairo_destroy(context);
            }
            if (surface != 0)
            {
                Gtk.cairo_surface_destroy(surface);
            }
            Gtk.cairo_surface_destroy(logo);
        }
    }

    public void Dispose() => _directory?.Delete(recursive: true);
}
