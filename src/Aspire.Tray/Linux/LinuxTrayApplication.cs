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
internal sealed class LinuxTrayApplication(TrayController controller, ITrayStartupSettings startup, TimeProvider timeProvider) : IDisposable
{
    private static LinuxTrayApplication? s_current;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Dictionary<nint, LinuxTrayMenuItem> _commands = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _desktopTasks = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private TrayViewState? _displayed;
    private nint _indicator;
    private nint _watcher;
    private nint _menu;
    private uint _timer;
    private nint _dialog;
    private int _quitRequested;
    private bool _quitting;
    private bool _connected;
    private Exception? _failure;
    private long? _lastPinPrune;
    private IReadOnlyList<LinuxFolderApplication> _applications = [];
    private LinuxTrayIcons? _icons;
    private TrayIconStatus? _iconStatus;
    private LinuxTrayPanel? _panel;
    private LinuxStatusNotifierItem? _panelIcon;

    internal LinuxTrayApplication(TrayController controller, ITrayStartupSettings startup)
        : this(controller, startup, TimeProvider.System)
    {
    }

    private bool IsQuitting => _quitting || Volatile.Read(ref _quitRequested) != 0;

    internal bool IsAvailable { get; private set; }

    internal unsafe void Run()
    {
        Gtk.Load();
        var nativeMenu = LinuxDesktopEnvironment.IsGnome(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"));
        if (!nativeMenu)
        {
            LinuxLayerShell.Preload();
        }
        if (Gtk.gtk_init_check(0, 0) == 0)
        {
            throw new InvalidOperationException("GTK could not connect to a graphical desktop. Run Aspire Tray inside your Wayland or X11 session.");
        }
        s_current = this;
        // The proxy follows PropertiesChanged and watcher owner changes on GTK's
        // main context. Do not start a watcher; reload invalidated properties.
        // https://docs.gtk.org/gio/class.DBusProxy.html
        _watcher = Gtk.g_dbus_proxy_new_for_bus_sync(2, 4 | 8, 0,
            "org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher", 0, out var error);
        if (_watcher == 0)
        {
            if (error != 0)
            {
                LinuxDesktop.g_error_free(error);
            }
            throw new InvalidOperationException("Could not observe the desktop's StatusNotifierWatcher on the session bus.");
        }
        var icon = Path.Combine(AppContext.BaseDirectory, "Aspire.png");
        if (!File.Exists(icon))
        {
            throw new FileNotFoundException("The tray icon is missing.", icon);
        }
        _icons = new LinuxTrayIcons(icon);
        _applications = LinuxFolderApplication.Discover(Environment.GetEnvironmentVariable("PATH") ?? "");
        if (!nativeMenu)
        {
            _panel = new(controller, _applications, ExecutePanelAction, LinuxDesktopEnvironment.RequirePanelBackend());
            _panelIcon = new(_watcher, icon, (x, y) => _pending.Enqueue(() =>
            {
                _panel.SetAnchor(new(x, y, 0, 0));
                _panel.Toggle();
            }));
        }
        else
        {
            _indicator = Gtk.app_indicator_new("dev.aspire.tray", icon, 0);
            if (_indicator == 0)
            {
                throw new InvalidOperationException("Could not create the Linux AppIndicator.");
            }
            Gtk.app_indicator_set_title(_indicator, "Aspire");
            Gtk.g_signal_connect_data(_indicator, "connection-changed",
                (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&ConnectionChanged, 0, 0, 0);
        }
        Refresh();
        if (_indicator != 0)
        {
            Gtk.app_indicator_set_status(_indicator, 1);
        }
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
            else if (!IsAvailable)
            {
                restored.TrySetException(new InvalidOperationException("No registered StatusNotifierHost is available for the tray. Enable the desktop's tray support."));
            }
            else
            {
                if (_panel is not null)
                {
                    _panel.Show();
                }
                else
                {
                    Gtk.app_indicator_set_status(_indicator, 0);
                    Gtk.app_indicator_set_status(_indicator, 1);
                }
                restored.TrySetResult();
            }
        });
        await restored.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal void RequestQuit() => Volatile.Write(ref _quitRequested, 1);

    internal void ProcessPending()
    {
        while (_pending.TryDequeue(out var action))
        {
            action();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ConnectionChanged(nint indicator, int connected, nint data)
    {
        // This confirms item registration, not that a panel host is available.
        s_current!._connected = connected != 0;
    }

    private bool HasRegisteredHost()
    {
        var value = Gtk.g_dbus_proxy_get_cached_property(_watcher, "IsStatusNotifierHostRegistered");
        if (value == 0)
        {
            return false;
        }
        try
        {
            if (Gtk.g_variant_is_of_type(value, "b") == 0)
            {
                throw new InvalidOperationException("The StatusNotifierWatcher's host registration property is not a boolean.");
            }
            return Gtk.g_variant_get_boolean(value) != 0;
        }
        finally
        {
            Gtk.g_variant_unref(value);
        }
    }

    internal void UpdateAvailability(bool watcherConnected, bool hostRegistered)
    {
        // Watcher registration alone can succeed without any panel displaying items.
        // https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/StatusNotifierWatcher/
        var available = watcherConnected && hostRegistered;
        if (IsAvailable && !available)
        {
            Console.Error.WriteLine("The desktop tray watcher or panel host disconnected. Waiting for it to return.");
        }
        IsAvailable = available;
        if (available)
        {
            _ready.TrySetResult();
        }
    }

    private void Quit()
    {
        _quitting = true;
        _panel?.Hide();
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
            if (app.IsQuitting)
            {
                app.Quit();
                return 1;
            }
            // Retain command identities during modal dialogs, but never defer shutdown.
            if (app._dialog != 0)
            {
                return 1;
            }
            app._panelIcon?.PollRegistration();
            app._panel?.Poll();
            app.UpdateAvailability(app._panelIcon?.IsRegistered ?? app._connected, app.HasRegisteredHost());
            if (!app.IsAvailable)
            {
                app._panel?.Hide();
            }
            app.ProcessPending();
            if (!app.IsQuitting)
            {
                if (!app._ready.Task.IsCompleted && Stopwatch.GetElapsedTime(app._started) > TimeSpan.FromSeconds(8))
                {
                    throw new InvalidOperationException("The tray requires StatusNotifierWatcher registration and a registered panel host. Enable an AppIndicator extension on GNOME or your desktop's tray module.");
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
        var state = GetStateForRefresh();
        if (state is null)
        {
            return;
        }
        var previous = _menu;
        _panel?.Update(state);
        if (_panelIcon is null)
        {
            _commands.Clear();
            _menu = BuildMenu(LinuxTrayMenu.Build(state, _applications));
            // Retain our own reference: replacing the indicator's menu drops its reference
            // before we disconnect/destroy the old widgets and their callbacks.
            Gtk.g_object_ref_sink(_menu);
            Gtk.gtk_widget_show_all(_menu);
            Gtk.app_indicator_set_menu(_indicator, _menu);
            Gtk.app_indicator_set_title(_indicator, $"Aspire - {state.Status}");
        }
        var status = TrayIconStatusRules.Get(state);
        if (_iconStatus != status)
        {
            var icon = _icons!.GetPath(status);
            var description = $"Aspire - {TrayIconStatusRules.Describe(status)}";
            if (_panelIcon is not null)
            {
                _panelIcon.Update(icon, description);
            }
            else
            {
                Gtk.app_indicator_set_icon_full(_indicator, icon, description);
            }
            _iconStatus = status;
        }
        if (previous != 0)
        {
            Gtk.gtk_widget_destroy(previous);
            Gtk.g_object_unref(previous);
        }
    }

    internal TrayViewState? GetStateForRefresh()
    {
        var now = timeProvider.GetTimestamp();
        // Discovery heartbeats do not publish new states. Probe pins independently,
        // but avoid filesystem access on every 100 ms GTK timer tick.
        if (_lastPinPrune is not { } previous || timeProvider.GetElapsedTime(previous, now) >= TimeSpan.FromSeconds(1))
        {
            controller.PruneMissingPinnedAppHosts();
            _lastPinPrune = now;
        }
        var state = controller.State;
        if (ReferenceEquals(_displayed, state))
        {
            return null;
        }
        _displayed = state;
        return state;
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
            if (!app.IsQuitting && app._dialog == 0
                && app._commands.TryGetValue(widget, out var item) && item.Enabled)
            {
                app.Execute(item);
            }
        }
        catch (Exception ex)
        {
            // Never include dashboard URLs or raw process output in diagnostics.
            Console.Error.WriteLine($"Tray action failed ({ex.GetType().Name}).");
            app.ShowActionFailure(ex);
        }
    }

    internal void Execute(LinuxTrayMenuItem item)
    {
        var host = item.Host;
        if (item.Action is LinuxTrayAction.Start or LinuxTrayAction.Folder or LinuxTrayAction.OpenIn
            && !File.Exists(host!.Id.AppHostPath))
        {
            if (!host.IsRunning && !host.IsPinned && Confirm("AppHost file not found",
                "Remove this missing project from recently opened?", "Remove"))
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
                controller.RequestStop(host!.Id);
                break;
            case LinuxTrayAction.Start:
                controller.RequestStart(host!.Id.AppHostPath);
                break;
            case LinuxTrayAction.TogglePin:
                controller.SetPinned(host!.Id.AppHostPath, !host.IsPinned);
                break;
            case LinuxTrayAction.RemoveRecent:
                controller.RemoveRecent(host!.Id.AppHostPath);
                break;
            case LinuxTrayAction.Folder:
                // File-manager activation can wait for D-Bus service startup. Keep
                // GTK responsive so stop and SIGTERM still interrupt open dialogs.
                _desktopTasks.RemoveAll(task => task.IsCompleted);
                _desktopTasks.Add(RevealFileAsync(host!.Id.AppHostPath));
                break;
            case LinuxTrayAction.CopyPath:
                Gtk.gtk_clipboard_set_text(Gtk.gtk_clipboard_get(Gtk.gdk_atom_intern("CLIPBOARD", 0)),
                    Path.GetDirectoryName(host!.Id.AppHostPath)!, -1);
                break;
            case LinuxTrayAction.OpenIn:
                var application = item.Application ?? throw new InvalidOperationException("The selected application is no longer available.");
                application.Open(Path.GetDirectoryName(host!.Id.AppHostPath)!);
                break;
            case LinuxTrayAction.ClearRecent:
                if (Confirm("Clear recently opened?", "This cannot be undone. Pinned AppHosts will be kept.", "Clear"))
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

    private async Task RevealFileAsync(string path)
    {
        try
        {
            await Task.Run(() => LinuxFileManager.ShowItem(path)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"File-manager action failed ({ex.GetType().Name}).");
            _pending.Enqueue(() =>
            {
                if (!IsQuitting)
                {
                    ShowActionFailure(ex);
                }
            });
        }
    }

    private void ShowActionFailure(Exception error)
    {
        var message = error is InvalidOperationException or FileNotFoundException
            ? error.Message : "The requested action failed. Check your desktop application configuration.";
        controller.ReportActionError(message);
        if (_panel is not null)
        {
            if (!_panel.Visible)
            {
                _panel.Show();
            }
            _panel.Update(controller.State);
        }
        else
        {
            ShowMessage("Aspire action failed", message);
        }
    }

    private void ExecutePanelAction(PanelAction action)
    {
        try
        {
            ExecutePanelCommand(action);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tray action failed ({ex.GetType().Name}).");
            ShowActionFailure(ex);
        }
    }

    private void ExecutePanelCommand(PanelAction action)
    {
        switch (action.Kind)
        {
            case PanelActionKind.Settings:
                _panel!.ShowSettings(PanelSettings.Read(startup, preview: false, LogPanelSettingsFailure));
                break;
            case PanelActionKind.SetLaunchAtSignIn:
                _panel!.UpdateSettings(PanelSettings.SetLaunchAtSignIn(startup, action.Enabled, preview: false, LogPanelSettingsFailure));
                break;
            case PanelActionKind.ClearRecent:
                controller.ClearRecent();
                break;
            case PanelActionKind.Close:
                _panel!.Hide();
                break;
            case PanelActionKind.ToggleRecent:
                break;
            default:
                if (action.Kind is PanelActionKind.Start or PanelActionKind.OpenIn or PanelActionKind.ShowInFolder
                    && !File.Exists(action.Host!.Id.AppHostPath))
                {
                    if (action.Recent)
                    {
                        _panel!.ConfirmRemoveMissing(action.Host);
                    }
                    else
                    {
                        controller.ReportActionError("The AppHost project no longer exists at its saved location.");
                        _panel!.Update(controller.State);
                    }
                    break;
                }
                if (action.Kind is PanelActionKind.OpenDashboard or PanelActionKind.Documentation or PanelActionKind.OpenIn
                    or PanelActionKind.ShowInFolder or PanelActionKind.Quit)
                {
                    _panel!.Hide();
                }
                var command = action.Kind switch
                {
                    PanelActionKind.OpenDashboard => LinuxTrayAction.Dashboard,
                    PanelActionKind.Stop => LinuxTrayAction.Stop,
                    PanelActionKind.Start => LinuxTrayAction.Start,
                    PanelActionKind.OpenIn => LinuxTrayAction.OpenIn,
                    PanelActionKind.ShowInFolder => LinuxTrayAction.Folder,
                    PanelActionKind.CopyPath => LinuxTrayAction.CopyPath,
                    PanelActionKind.TogglePin => LinuxTrayAction.TogglePin,
                    PanelActionKind.RemoveRecent => LinuxTrayAction.RemoveRecent,
                    PanelActionKind.Documentation => LinuxTrayAction.Documentation,
                    PanelActionKind.Quit => LinuxTrayAction.Quit,
                    _ => throw new InvalidOperationException("The selected flyout action is not supported.")
                };
                Execute(new("", command, action.Host)
                {
                    Application = action.Kind == PanelActionKind.OpenIn
                        ? _applications.SingleOrDefault(application => application.Title == action.Application) : null
                });
                break;
        }
    }

    private static void LogPanelSettingsFailure(Exception error)
        => Console.Error.WriteLine($"Flyout settings failed ({error.GetType().Name}).");

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
        => RunDialog(dialog, static dialog =>
        {
            Gtk.gtk_widget_show_all(dialog);
            return Gtk.gtk_dialog_run(dialog);
        });

    /// <summary>
    /// Runs a modal dialog while rejecting responses after shutdown is requested.
    /// </summary>
    internal int RunDialog(nint dialog, Func<nint, int> runDialog)
    {
        if (IsQuitting)
        {
            return -6;
        }
        _panel?.Hide();
        _dialog = dialog;
        try
        {
            var response = runDialog(dialog);
            // IPC/signals can request shutdown before GTK's timer processes it.
            // Do not commit an affirmative response during that interval.
            return IsQuitting ? -6 : response;
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

    private bool Confirm(string title, string text, string affirmative)
    {
        var dialog = CreateDialog(title, text);
        try
        {
            Gtk.gtk_dialog_add_button(dialog, "Cancel", -6);
            Gtk.gtk_dialog_add_button(dialog, affirmative, -3);
            Gtk.gtk_dialog_set_default_response(dialog, -6);
            return RunDialog(dialog) == -3;
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
        var dialog = CreateDialog(TraySettingsText.PanelTitle,
            $"Aspire Tray\n{TraySettingsText.GetVersionText()}\n{TraySettingsText.GetAboutDescription(preview: false)}\n\n{state.Detail}");
        try
        {
            var content = Gtk.gtk_dialog_get_content_area(dialog);
            var login = Gtk.gtk_check_button_new_with_label(TraySettingsText.PanelStartupOption);
            LinuxDesktop.gtk_toggle_button_set_active(login, state.Enabled ? 1 : 0);
            Gtk.gtk_widget_set_sensitive(login, state.CanEnable || state.Enabled ? 1 : 0);
            Gtk.gtk_container_add(content, login);
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
            }
        }
        finally
        {
            Gtk.gtk_widget_destroy(dialog);
        }
    }

    public void Dispose()
    {
        Task.WhenAll(_desktopTasks).GetAwaiter().GetResult();
        if (_timer != 0)
        {
            Gtk.g_source_remove(_timer);
        }
        _panelIcon?.Dispose();
        _panel?.Dispose();
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
        if (_watcher != 0)
        {
            Gtk.g_object_unref(_watcher);
        }
        _icons?.Dispose();
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
