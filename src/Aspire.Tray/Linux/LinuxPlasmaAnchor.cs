// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>Maps Plasma's per-output native tray coordinates into GTK's desktop coordinates.</summary>
internal static partial class LinuxPlasmaAnchor
{
    internal static bool IsRequired(string? desktop, string? sessionType) =>
        sessionType?.Equals("wayland", StringComparison.OrdinalIgnoreCase) == true
        && desktop?.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Equals("KDE", StringComparison.OrdinalIgnoreCase)) == true;

    internal static PanelRect Translate(PanelRect anchor)
    {
        var outputs = ParseOutputs(ReadOutputInformation());
        var display = LinuxPanelNative.gdk_display_get_default();
        var primary = LinuxPanelNative.gdk_display_get_primary_monitor(display);
        var monitors = Enumerable.Range(0, LinuxPanelNative.gdk_display_get_n_monitors(display)).Select(index =>
        {
            var monitor = LinuxPanelNative.gdk_display_get_monitor(display, index);
            LinuxPanelNative.gdk_monitor_get_geometry(monitor, out var geometry);
            return new Monitor(Marshal.PtrToStringUTF8(gdk_monitor_get_model(monitor)) ?? "",
                new(geometry.X, geometry.Y, geometry.Width, geometry.Height), monitor == primary);
        }).ToArray();
        var pointer = gdk_seat_get_pointer(LinuxPanelNative.gdk_display_get_default_seat(display));
        (double X, double Y)? position = null;
        if (pointer != 0)
        {
            gdk_device_get_position(pointer, out var screen, out var x, out var y);
            if (screen != 0)
            {
                position = (x, y);
            }
        }

        return Map(anchor, outputs, monitors, UsesNativeCoordinates(), position);
    }

    internal static PanelRect Map(PanelRect anchor, IReadOnlyList<Output> outputs, IReadOnlyList<Monitor> monitors,
        bool nativeCoordinates, (double X, double Y)? pointer)
    {
        // Plasma uses the output's logical origin but native pixels within it:
        //   host = logicalOrigin + (logicalPoint - logicalOrigin) * outputScale.
        // XWayland instead scales the whole desktop by a single global factor.
        // GTK on Wayland already uses logical coordinates. Map through the matched
        // output geometry, never through font DPI (which also includes text scaling).
        // https://invent.kde.org/plasma/plasma-workspace/-/blob/master/applets/systemtray/systemtray.cpp
        var candidates = new List<(PanelRect Bounds, Monitor Monitor)>();
        foreach (var output in outputs)
        {
            var factor = nativeCoordinates ? output.Scale : 1;
            var originScale = nativeCoordinates && outputs.Count == 1 ? factor : 1;
            var native = new PanelRect(output.Logical.X * originScale, output.Logical.Y * originScale,
                output.Logical.Width * factor, output.Logical.Height * factor);
            if (!native.Contains(anchor.CenterX, anchor.CenterY))
            {
                continue;
            }
            var monitor = monitors.SingleOrDefault(monitor => monitor.Name == output.Name)
                ?? monitors.SingleOrDefault(monitor => monitor.Geometry.X == output.Logical.X
                    && monitor.Geometry.Y == output.Logical.Y);
            if (monitor is null)
            {
                throw new InvalidOperationException($"GTK did not expose the Plasma output '{output.Name}'.");
            }
            var scaleX = monitor.Geometry.Width / native.Width;
            var scaleY = monitor.Geometry.Height / native.Height;
            candidates.Add((new(monitor.Geometry.X + (anchor.X - native.X) * scaleX,
                monitor.Geometry.Y + (anchor.Y - native.Y) * scaleY, anchor.Width * scaleX, anchor.Height * scaleY), monitor));
        }
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("The tray icon position does not belong to an active Plasma output.");
        }
        if (candidates.Count == 1)
        {
            return candidates[0].Bounds;
        }
        // Native output rectangles can overlap even when logical outputs do not.
        // A pointer beside the translated icon disambiguates mouse activation;
        // primary-output preference also supports keyboard activation without
        // moving the flyout to an unrelated pointer elsewhere on the desktop.
        if (pointer is { } point)
        {
            var nearest = candidates.MinBy(candidate => Math.Pow(candidate.Bounds.CenterX - point.X, 2)
                + Math.Pow(candidate.Bounds.CenterY - point.Y, 2));
            if (Math.Abs(nearest.Bounds.CenterX - point.X) <= 4 && Math.Abs(nearest.Bounds.CenterY - point.Y) <= 4)
            {
                return nearest.Bounds;
            }
        }

        return candidates.FirstOrDefault(candidate => candidate.Monitor.Primary, candidates[0]).Bounds;
    }

    private static bool UsesNativeCoordinates()
    {
        var home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        var directories = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
        if (string.IsNullOrEmpty(directories))
        {
            directories = "/etc/xdg";
        }
        foreach (var directory in new[] { home }.Concat(directories.Split(':', StringSplitOptions.RemoveEmptyEntries)))
        {
            try
            {
                using var reader = File.OpenText(Path.Combine(directory, "kdeglobals"));
                if (ParseNativeCoordinates(reader) is { } enabled)
                {
                    return enabled;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // An absent setting uses Plasma's default; other read failures
                // must not silently select the wrong coordinate system.
            }
        }

        return true;
    }

    internal static bool? ParseNativeCoordinates(TextReader reader)
    {
        // Plasma reads kdeglobals, not font DPI or kwinrc:
        //   [KScreen]
        //   XwaylandClientsScale=false
        // When disabled, Activate contains logical coordinates instead of pixels.
        // https://invent.kde.org/plasma/plasma-workspace/-/blob/master/applets/systemtray/systemtray.cpp
        var section = false;
        bool? result = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line is "[KScreen]" or "[KScreen][$i]";
            }
            else if (section)
            {
                var fields = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (fields.Length == 2 && fields[0] is "XwaylandClientsScale" or "XwaylandClientsScale[$i]")
                {
                    result = bool.TryParse(fields[1], out var enabled) ? enabled
                        : throw new InvalidOperationException("Plasma has an invalid XWayland client scaling setting.");
                }
            }
        }

        return result;
    }

    internal static IReadOnlyList<Output> ParseOutputs(string information)
    {
        // KWin's supportInformation output contains blocks such as:
        //   Screen 1:
        //   ---------
        //   Name: DP-5
        //   Enabled: 1
        //   Geometry: 1920,0,3072x1728
        //   Physical size: 697x392mm
        //   Scale: 1.25
        // Other diagnostic sections/fields are unrelated. Disabled outputs need
        // no geometry; enabled outputs must provide every coordinate-map field.
        // https://invent.kde.org/plasma/kwin/-/blob/master/src/workspace.cpp
        var outputs = new List<Output>();
        using var reader = new StringReader(information);
        string? name = null;
        PanelRect? geometry = null;
        double? scale = null;
        var enabled = false;
        var screens = false;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line == "Screens")
            {
                screens = true;
                continue;
            }
            if (!screens)
            {
                continue;
            }
            if (line == "Compositing" || (line.StartsWith("Screen ", StringComparison.Ordinal) && line.EndsWith(':')))
            {
                AddOutput();
                name = null;
                geometry = null;
                scale = null;
                enabled = false;
                if (line == "Compositing")
                {
                    break;
                }
            }
            else if (line.StartsWith("Name: ", StringComparison.Ordinal))
            {
                name = line[6..];
            }
            else if (line.StartsWith("Enabled: ", StringComparison.Ordinal))
            {
                enabled = line[9..] switch
                {
                    "1" => true,
                    "0" => false,
                    _ => throw new InvalidOperationException("KWin reported an invalid output enabled state.")
                };
            }
            else if (line.StartsWith("Geometry: ", StringComparison.Ordinal))
            {
                var fields = line[10..].Split([',', 'x']);
                if (fields.Length != 4 || !fields.All(value => double.TryParse(value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)))
                {
                    throw new InvalidOperationException("KWin reported an invalid output geometry.");
                }
                var values = fields.Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                geometry = new(values[0], values[1], values[2], values[3]);
            }
            else if (line.StartsWith("Scale: ", StringComparison.Ordinal))
            {
                if (!double.TryParse(line[7..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !double.IsFinite(value) || value <= 0)
                {
                    throw new InvalidOperationException("KWin reported an invalid output scale.");
                }
                scale = value;
            }
        }
        AddOutput();
        if (outputs.Count == 0)
        {
            throw new InvalidOperationException("KWin did not provide any active output geometry.");
        }

        return outputs;

        void AddOutput()
        {
            if (!enabled)
            {
                return;
            }
            if (string.IsNullOrEmpty(name) || geometry is not { Width: > 0, Height: > 0 } bounds || scale is not { } factor)
            {
                throw new InvalidOperationException("KWin did not provide complete output geometry and scaling.");
            }
            outputs.Add(new(name, bounds, factor));
            enabled = false;
        }
    }

    private static string ReadOutputInformation()
    {
        var connection = g_bus_get_sync(2, 0, out var error);
        if (connection == 0)
        {
            FreeError(error);
            throw new InvalidOperationException("Could not connect to KWin for the tray icon position.");
        }
        try
        {
            // Read current output geometry on activation, not a cached primary
            // output. Do not start a compositor or wait indefinitely on the UI thread.
            var reply = g_dbus_connection_call_sync(connection, "org.kde.KWin", "/KWin", "org.kde.KWin",
                "supportInformation", 0, 0, 1, 1000, 0, out error);
            if (reply == 0)
            {
                FreeError(error);
                throw new InvalidOperationException("KWin could not provide the tray output's coordinate mapping.");
            }
            try
            {
                if (g_variant_is_of_type(reply, "(s)") == 0)
                {
                    throw new InvalidOperationException("KWin returned an invalid output-information response.");
                }
                var value = g_variant_get_child_value(reply, 0);
                try
                {
                    return Marshal.PtrToStringUTF8(g_variant_get_string(value, 0))
                        ?? throw new InvalidOperationException("KWin returned no output information.");
                }
                finally
                {
                    Gtk.g_variant_unref(value);
                }
            }
            finally
            {
                Gtk.g_variant_unref(reply);
            }
        }
        finally
        {
            Gtk.g_object_unref(connection);
        }
    }

    private static void FreeError(nint error)
    {
        if (error != 0)
        {
            LinuxDesktop.g_error_free(error);
        }
    }

    [LibraryImport("libgdk-3.so.0")] private static partial nint gdk_seat_get_pointer(nint seat);
    [LibraryImport("libgdk-3.so.0")] private static partial void gdk_device_get_position(nint device, out nint screen, out int x, out int y);
    [LibraryImport("libgdk-3.so.0")] private static partial nint gdk_monitor_get_model(nint monitor);
    [LibraryImport("libgio-2.0.so.0")] private static partial nint g_bus_get_sync(int busType, nint cancellable, out nint error);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_connection_call_sync(nint connection, string name, string path, string interfaceName,
        string method, nint parameters, nint replyType, int flags, int timeout, nint cancellable, out nint error);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial int g_variant_is_of_type(nint value, string type);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_get_child_value(nint value, nuint index);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_get_string(nint value, nint length);

    internal sealed record Output(string Name, PanelRect Logical, double Scale);
    internal sealed record Monitor(string Name, PanelRect Geometry, bool Primary);
}
