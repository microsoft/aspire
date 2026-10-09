// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>
/// GTK 3 widgets exported as StatusNotifierItem and com.canonical.dbusmenu by libappindicator.
/// </summary>
internal static partial class Gtk
{
    private const string GtkLibrary = "libgtk-3.so.0";
    private const string IndicatorLibrary = "aspire-appindicator";

    internal static void Load()
    {
        // Both implementations expose the same GTK3 ABI. Let the distribution supply
        // its maintained D-Bus menu implementation, including watcher restart handling.
        // https://github.com/AyatanaIndicators/libayatana-appindicator
        NativeLibrary.SetDllImportResolver(typeof(Gtk).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? paths)
    {
        if (name != IndicatorLibrary)
        {
            return 0;
        }
        foreach (var library in new[] { "libayatana-appindicator3.so.1", "libappindicator3.so.1" })
        {
            if (NativeLibrary.TryLoad(library, assembly, paths, out var handle))
            {
                return handle;
            }
        }
        throw new DllNotFoundException("Install the GTK 3 Ayatana AppIndicator library (libayatana-appindicator3-1 on Debian/Ubuntu, libayatana-appindicator-gtk3 on Fedora), or libappindicator3.");
    }

    [LibraryImport(GtkLibrary)] internal static partial int gtk_init_check(nint argc, nint argv);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_main();
    [LibraryImport(GtkLibrary)] internal static partial void gtk_main_quit();
    [LibraryImport(GtkLibrary)] internal static partial nint gtk_menu_new();
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_menu_item_new_with_label(string label);
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_image_menu_item_new_with_label(string label);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_image_menu_item_set_image(nint item, nint image);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_image_menu_item_set_always_show_image(nint item, int alwaysShow);
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_image_new_from_icon_name(string name, int size);
    [LibraryImport(GtkLibrary)] internal static partial nint gtk_image_new_from_pixbuf(nint pixbuf);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_pixbuf_get_from_surface(nint surface, int x, int y, int width, int height);
    [LibraryImport("libcairo.so.2")] internal static partial nint cairo_image_surface_create(int format, int width, int height);
    [LibraryImport("libcairo.so.2")] internal static partial int cairo_surface_status(nint surface);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_surface_destroy(nint surface);
    [LibraryImport("libcairo.so.2")] internal static partial nint cairo_create(nint surface);
    [LibraryImport("libcairo.so.2")] internal static partial int cairo_status(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_destroy(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_set_source_rgb(nint context, double red, double green, double blue);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_arc(nint context, double x, double y, double radius, double start, double end);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_rectangle(nint context, double x, double y, double width, double height);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_fill(nint context);
    [LibraryImport(GtkLibrary)] internal static partial nint gtk_separator_menu_item_new();
    [LibraryImport(GtkLibrary)] internal static partial void gtk_menu_shell_append(nint menu, nint item);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_menu_item_set_submenu(nint item, nint submenu);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_widget_set_sensitive(nint widget, int sensitive);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_widget_show_all(nint widget);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_widget_destroy(nint widget);
    [LibraryImport(GtkLibrary)] internal static partial nint gtk_dialog_new();
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_window_set_title(nint window, string title);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_window_set_modal(nint window, int modal);
    [LibraryImport(GtkLibrary)] internal static partial nint gtk_dialog_get_content_area(nint dialog);
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_label_new(string text);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_label_set_line_wrap(nint label, int wrap);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_label_set_max_width_chars(nint label, int width);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_container_add(nint container, nint child);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_container_set_border_width(nint container, uint border);
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_dialog_add_button(nint dialog, string label, int response);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_dialog_set_default_response(nint dialog, int response);
    [LibraryImport(GtkLibrary)] internal static partial int gtk_dialog_run(nint dialog);
    [LibraryImport(GtkLibrary)] internal static partial void gtk_dialog_response(nint dialog, int response);
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_check_button_new_with_label(string label);
    [LibraryImport(GtkLibrary)] internal static partial int gtk_toggle_button_get_active(nint button);
    [LibraryImport(GtkLibrary)] internal static partial nint gtk_clipboard_get(nint selection);
    [LibraryImport("libgdk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gdk_atom_intern(string name, int onlyIfExists);
    [LibraryImport(GtkLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_clipboard_set_text(nint clipboard, string text, int length);
    [LibraryImport("libglib-2.0.so.0")] internal static partial uint g_timeout_add(uint interval, nint callback, nint data);
    [LibraryImport("libglib-2.0.so.0")] internal static partial int g_source_remove(uint source);
    [LibraryImport("libgobject-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial ulong g_signal_connect_data(nint instance, string signal, nint callback, nint data, nint destroy, int flags);
    [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint instance);
    [LibraryImport("libgobject-2.0.so.0")] internal static partial nint g_object_ref_sink(nint instance);
    [LibraryImport(IndicatorLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint app_indicator_new(string id, string icon, int category);
    [LibraryImport(IndicatorLibrary)] internal static partial void app_indicator_set_status(nint indicator, int status);
    [LibraryImport(IndicatorLibrary)] internal static partial void app_indicator_set_menu(nint indicator, nint menu);
    [LibraryImport(IndicatorLibrary, StringMarshalling = StringMarshalling.Utf8)] internal static partial void app_indicator_set_title(nint indicator, string title);
}
