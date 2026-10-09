// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>
/// Owns GTK on the initial thread; AppIndicator exports its menus over the session bus.
/// </summary>
internal sealed class LinuxTrayApplication(TrayController controller, ITrayStartupSettings startup) : IDisposable
{
    private static LinuxTrayApplication? s_current;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Dictionary<nint, LinuxTrayMenuItem> _commands = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _code = FindExecutable("code");
    private readonly long _started = Stopwatch.GetTimestamp();
    private TrayViewState? _displayed;
    private nint _indicator;
    private nint _menu;
    private uint _timer;
    private nint _dialog;
    private int _quitRequested;
    private bool _quitting;
    private bool _connected;
    private bool _reportedConnected;
    private Exception? _failure;

    internal unsafe void Run()
    {
        Gtk.Load();
        if (Gtk.gtk_init_check(0, 0) == 0)
        {
            throw new InvalidOperationException("GTK could not connect to a graphical desktop. Run Aspire Tray inside your Wayland or X11 session.");
        }
        s_current = this;
        var icon = Path.Combine(AppContext.BaseDirectory, "Aspire.png");
        if (!File.Exists(icon))
        {
            throw new FileNotFoundException("The tray icon is missing.", icon);
        }
        _indicator = Gtk.app_indicator_new("dev.aspire.tray", icon, 0);
        if (_indicator == 0)
        {
            throw new InvalidOperationException("Could not create the Linux AppIndicator.");
        }
        Gtk.app_indicator_set_title(_indicator, "Aspire");
        Gtk.g_signal_connect_data(_indicator, "connection-changed",
            (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&ConnectionChanged, 0, 0, 0);
        Refresh();
        Gtk.app_indicator_set_status(_indicator, 1);
        _timer = Gtk.g_timeout_add(100, (nint)(delegate* unmanaged[Cdecl]<nint, int>)&Tick, 0);
        Gtk.gtk_main();
        if (_failure is not null)
        {
            throw new InvalidOperationException($"The Linux tray UI failed: {_failure.Message}", _failure);
        }
    }

    internal Task ReadyAsync(CancellationToken token) => _ready.Task.WaitAsync(token);

    internal async Task RestoreAsync(CancellationToken token)
    {
        await ReadyAsync(token).ConfigureAwait(false);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue(() =>
        {
            if (token.IsCancellationRequested)
            {
                restored.TrySetCanceled(token);
            }
            else if (!_connected)
            {
                restored.TrySetException(new InvalidOperationException("No StatusNotifierWatcher is available. Enable the desktop's tray support."));
            }
            else
            {
                Gtk.app_indicator_set_status(_indicator, 0);
                Gtk.app_indicator_set_status(_indicator, 1);
                restored.TrySetResult();
            }
        });
        await restored.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal void RequestQuit() => Volatile.Write(ref _quitRequested, 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ConnectionChanged(nint indicator, int connected, nint data)
    {
        // Successful watcher registration is reported by this signal, not a getter.
        s_current!._connected = connected != 0;
    }

    private void Quit()
    {
        _quitting = true;
        if (_dialog != 0)
        {
            // gtk_dialog_run owns a separate nested loop. Cancel it before quitting
            // gtk_main so confirmations cannot commit actions during shutdown.
            // https://docs.gtk.org/gtk3/method.Dialog.run.html
            Gtk.gtk_dialog_response(_dialog, -6);
        }
        else
        {
            Gtk.gtk_main_quit();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Tick(nint data)
    {
        var app = s_current!;
        try
        {
            if (app._quitting || Volatile.Read(ref app._quitRequested) != 0)
            {
                app.Quit();
                return 1;
            }
            // Retain command identities during modal dialogs, but never defer shutdown.
            if (app._dialog != 0)
            {
                return 1;
            }
            while (app._pending.TryDequeue(out var action))
            {
                action();
            }
            if (!app._quitting)
            {
                if (app._reportedConnected && !app._connected)
                {
                    Console.Error.WriteLine("The desktop tray watcher disconnected. Waiting for it to return.");
                }
                app._reportedConnected = app._connected;
                if (app._connected)
                {
                    app._ready.TrySetResult();
                }
                if (!app._ready.Task.IsCompleted && Stopwatch.GetElapsedTime(app._started) > TimeSpan.FromSeconds(8))
                {
                    throw new InvalidOperationException("No StatusNotifierWatcher registered the tray. Enable an AppIndicator extension on GNOME or the tray module on Waybar.");
                }
                app.Refresh();
            }
        }
        catch (Exception ex)
        {
            app._failure = ex;
            app._ready.TrySetException(ex);
            app.Quit();
        }
        return 1;
    }

    private void Refresh()
    {
        var state = controller.State;
        if (ReferenceEquals(_displayed, state))
        {
            return;
        }
        controller.PruneMissingPinnedAppHosts();
        state = controller.State;
        var previous = _menu;
        _commands.Clear();
        _menu = BuildMenu(LinuxTrayMenu.Build(state, controller.ConfirmStop, _code is not null));
        // Retain our own reference: replacing the indicator's menu drops its reference
        // before we disconnect/destroy the old widgets and their callbacks.
        Gtk.g_object_ref_sink(_menu);
        Gtk.gtk_widget_show_all(_menu);
        Gtk.app_indicator_set_menu(_indicator, _menu);
        Gtk.app_indicator_set_title(_indicator, $"Aspire - {state.Status}");
        if (previous != 0)
        {
            Gtk.gtk_widget_destroy(previous);
            Gtk.g_object_unref(previous);
        }
        _displayed = state;
    }

    private unsafe nint BuildMenu(IReadOnlyList<LinuxTrayMenuItem> items)
    {
        var menu = Gtk.gtk_menu_new();
        foreach (var item in items)
        {
            var widget = item.Action == LinuxTrayAction.Separator ? Gtk.gtk_separator_menu_item_new()
                : item.Icon == LinuxTrayIcon.None ? Gtk.gtk_menu_item_new_with_label(item.Label)
                : Gtk.gtk_image_menu_item_new_with_label(item.Label);
            if (item.Icon != LinuxTrayIcon.None)
            {
                // libdbusmenu's GTK parser recognizes GtkImageMenuItem, not an arbitrary
                // image/label box. Keep using this GTK 3 widget for icon-name/icon-data export.
                // https://git.launchpad.net/libdbusmenu/tree/libdbusmenu-gtk/parser.c
                var image = item.Icon switch
                {
                    LinuxTrayIcon.Documentation => Gtk.gtk_image_new_from_icon_name("help-browser", 1),
                    LinuxTrayIcon.Settings => Gtk.gtk_image_new_from_icon_name("preferences-system", 1),
                    _ => CreateHealthImage(item.Icon)
                };
                Gtk.gtk_image_menu_item_set_image(widget, image);
                Gtk.gtk_image_menu_item_set_always_show_image(widget, 1);
            }
            Gtk.gtk_widget_set_sensitive(widget, item.Enabled ? 1 : 0);
            Gtk.gtk_menu_shell_append(menu, widget);
            if (item.Children is not null)
            {
                Gtk.gtk_menu_item_set_submenu(widget, BuildMenu(item.Children));
            }
            else if (item.Action is not (LinuxTrayAction.None or LinuxTrayAction.Separator))
            {
                _commands.Add(widget, item);
                Gtk.g_signal_connect_data(widget, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Activate, 0, 0, 0);
            }
        }
        return menu;
    }

    private static nint CreateHealthImage(LinuxTrayIcon icon)
    {
        // Cairo is already a GTK dependency. Export a small antialiased PNG through
        // GdkPixbuf so health colors do not depend on the panel's installed icon theme.
        const int size = 16;
        var surface = Gtk.cairo_image_surface_create(0, size, size); // CAIRO_FORMAT_ARGB32
        var context = Gtk.cairo_create(surface);
        try
        {
            var color = icon switch
            {
                LinuxTrayIcon.Healthy => 0x269653,
                LinuxTrayIcon.Warning => 0xE58A00,
                LinuxTrayIcon.Unhealthy => 0xD63E42,
                _ => 0x929292
            };
            Draw(size * 0.3, 0x606060);
            Draw(size * 0.3 - 1, color);
            if (Gtk.cairo_surface_status(surface) != 0 || Gtk.cairo_status(context) != 0)
            {
                throw new InvalidOperationException("Could not render the AppHost health icon.");
            }
            var pixels = Gtk.gdk_pixbuf_get_from_surface(surface, 0, 0, size, size);
            if (pixels == 0)
            {
                throw new InvalidOperationException("Could not create the AppHost health image.");
            }
            try
            {
                return Gtk.gtk_image_new_from_pixbuf(pixels);
            }
            finally
            {
                Gtk.g_object_unref(pixels);
            }
        }
        finally
        {
            Gtk.cairo_destroy(context);
            Gtk.cairo_surface_destroy(surface);
        }

        void Draw(double radius, int color)
        {
            Gtk.cairo_set_source_rgb(context, ((color >> 16) & 255) / 255d, ((color >> 8) & 255) / 255d, (color & 255) / 255d);
            if (icon == LinuxTrayIcon.Stopped)
            {
                Gtk.cairo_rectangle(context, size / 2d - radius, size / 2d - radius, radius * 2, radius * 2);
            }
            else
            {
                Gtk.cairo_arc(context, size / 2d, size / 2d, radius, 0, Math.Tau);
            }
            Gtk.cairo_fill(context);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Activate(nint widget, nint data)
    {
        var app = s_current!;
        try
        {
            if (!app._quitting && app._dialog == 0
                && app._commands.TryGetValue(widget, out var item) && item.Enabled)
            {
                app.Execute(item);
            }
        }
        catch (Exception ex)
        {
            // Never include dashboard URLs or raw process output in diagnostics.
            Console.Error.WriteLine($"Tray action failed ({ex.GetType().Name}).");
            app.ShowMessage("Aspire action failed", ex is InvalidOperationException or FileNotFoundException
                ? ex.Message : "The requested action failed. Check your desktop application configuration.");
        }
    }

    private void Execute(LinuxTrayMenuItem item)
    {
        var host = item.Host;
        if (item.Action is LinuxTrayAction.Start or LinuxTrayAction.Folder or LinuxTrayAction.Code
            && !File.Exists(host!.Id.AppHostPath))
        {
            if (!host.IsRunning && !host.IsPinned && Confirm("AppHost file not found",
                "Remove this missing project from recently opened?", "Remove", offerSkip: false, out _))
            {
                controller.RemoveRecent(host.Id.AppHostPath);
            }
            else
            {
                ShowMessage("AppHost file not found", "The AppHost project no longer exists at its saved location.");
            }
            return;
        }
        switch (item.Action)
        {
            case LinuxTrayAction.Dashboard:
                OpenUri(controller.GetDashboardUri(host!.Id).AbsoluteUri);
                break;
            case LinuxTrayAction.Stop:
                controller.RequireLiveInstance(host!.Id);
                var skipWarning = false;
                if (!controller.ConfirmStop || Confirm("Stop AppHost?",
                    $"Stop {host.DisplayName} (PID {host.Id.AppHostPid})?", "Stop", offerSkip: true, out skipWarning))
                {
                    controller.RequestStop(host.Id);
                    if (skipWarning)
                    {
                        controller.SetConfirmStop(false);
                    }
                }
                break;
            case LinuxTrayAction.Start:
                controller.RequestStart(host!.Id.AppHostPath);
                break;
            case LinuxTrayAction.TogglePin:
                controller.SetPinned(host!.Id.AppHostPath, !host.IsPinned);
                break;
            case LinuxTrayAction.Folder:
                var folder = Path.GetDirectoryName(host!.Id.AppHostPath)!;
                if (!Directory.Exists(folder))
                {
                    throw new FileNotFoundException("The AppHost folder no longer exists.", folder);
                }
                OpenUri(new Uri(folder + Path.DirectorySeparatorChar).AbsoluteUri);
                break;
            case LinuxTrayAction.CopyPath:
                Gtk.gtk_clipboard_set_text(Gtk.gtk_clipboard_get(Gtk.gdk_atom_intern("CLIPBOARD", 0)),
                    Path.GetDirectoryName(host!.Id.AppHostPath)!, -1);
                break;
            case LinuxTrayAction.Code:
                var info = new ProcessStartInfo(_code!) { UseShellExecute = false };
                info.ArgumentList.Add("--");
                info.ArgumentList.Add(Path.GetDirectoryName(host!.Id.AppHostPath)!);
                using (Process.Start(info) ?? throw new InvalidOperationException("Could not open Visual Studio Code."))
                {
                }
                break;
            case LinuxTrayAction.ClearRecent:
                if (Confirm("Clear recently opened?", "This cannot be undone. Pinned AppHosts will be kept.", "Clear", offerSkip: false, out _))
                {
                    controller.ClearRecent();
                }
                break;
            case LinuxTrayAction.Settings:
                ShowSettings();
                break;
            case LinuxTrayAction.Documentation:
                OpenUri("https://aspire.dev");
                break;
            case LinuxTrayAction.Quit:
                Quit();
                break;
        }
    }

    private static void OpenUri(string uri)
    {
        if (LinuxDesktop.g_app_info_launch_default_for_uri(uri, 0, out var error) == 0)
        {
            if (error != 0)
            {
                LinuxDesktop.g_error_free(error);
            }
            throw new InvalidOperationException("The desktop could not open the requested location. Configure a default browser or file manager.");
        }
    }

    private static nint CreateDialog(string title, string text)
    {
        var dialog = Gtk.gtk_dialog_new();
        Gtk.gtk_window_set_title(dialog, title);
        Gtk.gtk_window_set_modal(dialog, 1);
        var content = Gtk.gtk_dialog_get_content_area(dialog);
        Gtk.gtk_container_set_border_width(content, 20);
        var label = Gtk.gtk_label_new(text);
        Gtk.gtk_label_set_line_wrap(label, 1);
        Gtk.gtk_label_set_max_width_chars(label, 60);
        Gtk.gtk_container_add(content, label);
        return dialog;
    }

    private int RunDialog(nint dialog)
    {
        if (_quitting)
        {
            return -6;
        }
        _dialog = dialog;
        try
        {
            Gtk.gtk_widget_show_all(dialog);
            return Gtk.gtk_dialog_run(dialog);
        }
        finally
        {
            _dialog = 0;
            if (_quitting)
            {
                Gtk.gtk_main_quit();
            }
        }
    }

    private bool Confirm(string title, string text, string affirmative, bool offerSkip, out bool skipWarning)
    {
        var dialog = CreateDialog(title, text);
        try
        {
            var toggle = offerSkip ? Gtk.gtk_check_button_new_with_label("Don't ask again") : 0;
            if (toggle != 0)
            {
                Gtk.gtk_container_add(Gtk.gtk_dialog_get_content_area(dialog), toggle);
            }
            Gtk.gtk_dialog_add_button(dialog, "Cancel", -6);
            Gtk.gtk_dialog_add_button(dialog, affirmative, -3);
            Gtk.gtk_dialog_set_default_response(dialog, -6);
            var accepted = RunDialog(dialog) == -3;
            skipWarning = accepted && toggle != 0 && Gtk.gtk_toggle_button_get_active(toggle) != 0;
            return accepted;
        }
        finally
        {
            Gtk.gtk_widget_destroy(dialog);
        }
    }

    private void ShowMessage(string title, string text)
    {
        var dialog = CreateDialog(title, text);
        try
        {
            Gtk.gtk_dialog_add_button(dialog, "Close", -7);
            RunDialog(dialog);
        }
        finally
        {
            Gtk.gtk_widget_destroy(dialog);
        }
    }

    private void ShowSettings()
    {
        var state = startup.Read();
        var dialog = CreateDialog(TraySettingsText.Title, TraySettingsText.GetAboutText(preview: false) + "\n\n" + state.Detail);
        try
        {
            var content = Gtk.gtk_dialog_get_content_area(dialog);
            var login = Gtk.gtk_check_button_new_with_label(TraySettingsText.StartupOption);
            LinuxDesktop.gtk_toggle_button_set_active(login, state.Enabled ? 1 : 0);
            Gtk.gtk_widget_set_sensitive(login, state.CanEnable || state.Enabled ? 1 : 0);
            Gtk.gtk_container_add(content, login);
            var confirm = Gtk.gtk_check_button_new_with_label("Ask before stopping an AppHost");
            LinuxDesktop.gtk_toggle_button_set_active(confirm, controller.ConfirmStop ? 1 : 0);
            Gtk.gtk_container_add(content, confirm);
            Gtk.gtk_dialog_add_button(dialog, "Cancel", -6);
            Gtk.gtk_dialog_add_button(dialog, "Save", -3);
            Gtk.gtk_dialog_set_default_response(dialog, -6);
            if (RunDialog(dialog) == -3)
            {
                var enabled = Gtk.gtk_toggle_button_get_active(login) != 0;
                if (enabled != state.Enabled)
                {
                    startup.SetEnabled(enabled);
                }
                controller.SetConfirmStop(Gtk.gtk_toggle_button_get_active(confirm) != 0);
            }
        }
        finally
        {
            Gtk.gtk_widget_destroy(dialog);
        }
    }

    private static string? FindExecutable(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(path => File.Exists(path) && (!OperatingSystem.IsLinux()
                || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0));

    public void Dispose()
    {
        if (_timer != 0)
        {
            Gtk.g_source_remove(_timer);
        }
        if (_indicator != 0)
        {
            Gtk.app_indicator_set_status(_indicator, 0);
            Gtk.g_object_unref(_indicator);
        }
        if (_menu != 0)
        {
            Gtk.gtk_widget_destroy(_menu);
            Gtk.g_object_unref(_menu);
        }
        s_current = null;
    }
}

internal static partial class LinuxDesktop
{
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int g_app_info_launch_default_for_uri(string uri, nint context, out nint error);
    [LibraryImport("libglib-2.0.so.0")] internal static partial void g_error_free(nint error);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_toggle_button_set_active(nint toggle, int active);
}
