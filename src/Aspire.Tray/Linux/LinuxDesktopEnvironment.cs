// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal enum LinuxPanelBackend
{
    X11,
    Wayland
}

/// <summary>Selects native menus for GNOME and compositor-compatible panel hosting elsewhere.</summary>
internal static partial class LinuxDesktopEnvironment
{
    internal static bool IsGnome(string? currentDesktop, string? sessionDesktop)
    {
        // XDG_CURRENT_DESKTOP is a colon-separated list, e.g. "ubuntu:GNOME".
        // It describes the active desktop; the session name is only a fallback.
        // https://specifications.freedesktop.org/desktop-entry-spec/latest/recognized-keys.html
        var desktop = string.IsNullOrWhiteSpace(currentDesktop) ? sessionDesktop : currentDesktop;
        return desktop?.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Equals("GNOME", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("GNOME-", StringComparison.OrdinalIgnoreCase)) == true;
    }

    internal static LinuxPanelBackend RequirePanelBackend()
    {
        var display = LinuxPanelNative.gdk_display_get_default();
        var type = Marshal.PtrToStringUTF8(g_type_name_from_instance(display));
        switch (type)
        {
            case "GdkX11Display":
                if (!IsX11Composited())
                {
                    throw new InvalidOperationException("The Aspire panel requires an X11 compositing manager for transparent popup windows.");
                }
                return LinuxPanelBackend.X11;
            case "GdkWaylandDisplay":
                if (!LinuxLayerShell.IsLoaded)
                {
                    throw new InvalidOperationException("The Aspire panel on Wayland requires libgtk-layer-shell.so.0. Install your distribution's gtk-layer-shell package.");
                }
                if (LinuxLayerShell.gtk_layer_is_supported() == 0)
                {
                    throw new InvalidOperationException("This Wayland compositor does not support the layer-shell protocol required by the Aspire panel.");
                }
                return LinuxPanelBackend.Wayland;
            default:
                throw new InvalidOperationException("The Aspire panel requires a composited X11 session or a layer-shell-compatible Wayland compositor.");
        }
    }

    internal static bool IsX11Composited() => gdk_screen_is_composited(
        gdk_display_get_default_screen(LinuxPanelNative.gdk_display_get_default())) != 0;

    [LibraryImport("libgobject-2.0.so.0")] private static partial nint g_type_name_from_instance(nint instance);
    [LibraryImport("libgdk-3.so.0")] private static partial nint gdk_display_get_default_screen(nint display);
    [LibraryImport("libgdk-3.so.0")] private static partial int gdk_screen_is_composited(nint screen);
}

/// <summary>Hosts Wayland popups using the compositor's layer-shell protocol.</summary>
internal static partial class LinuxLayerShell
{
    private static nint s_library;

    internal static bool IsLoaded => s_library != 0;

    internal static void Preload()
    {
        // Load before GTK so layer-shell's Wayland integration participates in
        // symbol resolution. Keep the library loaded for cached P/Invoke targets.
        // https://github.com/wmww/gtk-layer-shell
        if (!IsLoaded)
        {
            NativeLibrary.TryLoad("libgtk-layer-shell.so.0", out s_library);
        }
    }

    internal static void Configure(nint window, nint monitor, bool keyboard)
    {
        gtk_layer_init_for_window(window);
        gtk_layer_set_namespace(window, "dev.aspire.tray");
        gtk_layer_set_layer(window, 3); // GTK_LAYER_SHELL_LAYER_OVERLAY
        gtk_layer_set_monitor(window, monitor);
        for (var edge = 0; edge < 4; edge++)
        {
            gtk_layer_set_anchor(window, edge, 1);
        }
        gtk_layer_set_exclusive_zone(window, 0);
        gtk_layer_set_keyboard_mode(window, keyboard ? 1 : 0); // EXCLUSIVE / NONE
    }

    [LibraryImport("libgtk-layer-shell.so.0")] internal static partial int gtk_layer_is_supported();
    [LibraryImport("libgtk-layer-shell.so.0")] private static partial void gtk_layer_init_for_window(nint window);
    [LibraryImport("libgtk-layer-shell.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void gtk_layer_set_namespace(nint window, string name);
    [LibraryImport("libgtk-layer-shell.so.0")] private static partial void gtk_layer_set_layer(nint window, int layer);
    [LibraryImport("libgtk-layer-shell.so.0")] private static partial void gtk_layer_set_monitor(nint window, nint monitor);
    [LibraryImport("libgtk-layer-shell.so.0")] private static partial void gtk_layer_set_anchor(nint window, int edge, int anchor);
    [LibraryImport("libgtk-layer-shell.so.0")] private static partial void gtk_layer_set_exclusive_zone(nint window, int zone);
    [LibraryImport("libgtk-layer-shell.so.0")] private static partial void gtk_layer_set_keyboard_mode(nint window, int mode);
}
