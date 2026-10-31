// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>Hosts the shared Aspire panel in compositor-compatible transparent popup surfaces.</summary>
internal sealed class LinuxTrayPanel : IDisposable
{
    private readonly TrayController _controller;
    private readonly Action<PanelAction> _execute;
    private readonly TrayPanel _panel;
    private readonly GCHandle _handle;
    private readonly LinuxPanelBackend _backend;
    private readonly List<Surface> _surfaces = [];
    private readonly Dictionary<nint, Surface> _eventSurfaces = [];
    private LinuxPanelAccessibility? _accessibility;
    private nint _window;
    private nint _area;
    private nint _logo;
    private nint _seat;
    private PanelRect _workarea;
    private PanelRect _bounds;
    private nint _monitor;
    private nuint _previousFocus;
    private int _anchorX;
    private int _anchorY;
    private bool _hasAnchor;
    private bool _grabbed;
    private long _hiddenAt;
    private bool _disposed;

    internal bool Visible { get; private set; }

    internal void Toggle()
    {
        if (Visible || Environment.TickCount64 - _hiddenAt < 250)
        {
            Hide();
            return;
        }

        Show();
    }

    internal void Poll()
    {
        if (Visible)
        {
            if (_backend == LinuxPanelBackend.X11 && !LinuxDesktopEnvironment.IsX11Composited())
            {
                ReportFailure(new InvalidOperationException("The X11 compositing manager stopped. Restart it before opening the Aspire panel."));
                return;
            }
            var display = LinuxPanelNative.gdk_display_get_default();
            if (LinuxPanelNative.gdk_display_get_n_monitors(display) != _surfaces.Count
                || _surfaces.Any(surface =>
                {
                    LinuxPanelNative.gdk_monitor_get_geometry(surface.Monitor, out var geometry);
                    return surface.Geometry != new PanelRect(geometry.X, geometry.Y, geometry.Width, geometry.Height);
                }))
            {
                Hide();
            }
        }
    }

    internal void SetAnchor(PanelRect anchor)
    {
        _anchorX = (int)Math.Round(anchor.CenterX);
        _anchorY = (int)Math.Round(anchor.Bottom);
        _hasAnchor = true;
    }

    internal LinuxTrayPanel(TrayController controller, IReadOnlyList<LinuxFolderApplication> applications,
        Action<PanelAction> execute, LinuxPanelBackend backend)
    {
        _controller = controller;
        _execute = execute;
        _backend = backend;
        _panel = new(controller.State, TrayPanelPalette.Dark)
        {
            FileManagerName = "file manager",
            QuitVerb = "Quit",
            OpenInApplications = applications.Select(application => application.Title).ToArray()
        };
        _handle = GCHandle.Alloc(this);
    }

    internal void Show()
    {
        if (LinuxDesktopEnvironment.RequirePanelBackend() != _backend)
        {
            throw new InvalidOperationException("The desktop display backend changed. Restart Aspire Tray.");
        }
        _controller.PruneMissingPinnedAppHosts();
        _panel.Update(_controller.State);
        _panel.Reset();
        _panel.FocusFirst();
        Position();
        EnsureSurfaces();
        _panel.Palette = ReadPalette();
        Visible = true;
        Layout();
        if (_backend == LinuxPanelBackend.X11 && !_grabbed)
        {
            LinuxPanelNative.XGetInputFocus(LinuxPanelNative.gdk_x11_display_get_xdisplay(
                LinuxPanelNative.gdk_display_get_default()), out _previousFocus, out _);
        }
        foreach (var surface in _surfaces.Where(surface => !surface.Main))
        {
            Gtk.gtk_widget_show_all(surface.Window);
        }
        Gtk.gtk_widget_show_all(_window);
        _accessibility!.Focus(_panel.FocusedTarget);
    }

    private void GrabInput()
    {
        var nativeWindow = LinuxPanelNative.gtk_widget_get_window(_window);
        var display = LinuxPanelNative.gdk_display_get_default();
        LinuxPanelNative.XSetInputFocus(LinuxPanelNative.gdk_x11_display_get_xdisplay(display),
            LinuxPanelNative.gdk_x11_window_get_xid(nativeWindow), 2, LinuxPanelNative.gdk_x11_get_server_time(nativeWindow));
        _seat = LinuxPanelNative.gdk_display_get_default_seat(LinuxPanelNative.gdk_display_get_default());
        var result = LinuxPanelNative.gdk_seat_grab(_seat, nativeWindow, 8, 0, 0, 0, 0, 0);
        if (result != 0)
        {
            Hide();
            throw new InvalidOperationException($"Could not acquire popup input (GDK grab status {result}).");
        }
        _grabbed = true;
    }

    internal void Hide() => Hide(restoreFocus: true);

    private void Hide(bool restoreFocus)
    {
        if (!Visible)
        {
            return;
        }
        Visible = false;
        _hiddenAt = Environment.TickCount64;
        if (_grabbed)
        {
            _grabbed = false;
            LinuxPanelNative.gdk_seat_ungrab(_seat);
        }
        foreach (var surface in _surfaces.Where(surface => !surface.Destroyed))
        {
            LinuxPanelNative.gtk_widget_hide(surface.Window);
        }
        if (_backend == LinuxPanelBackend.X11 && restoreFocus && _previousFocus != 0)
        {
            var display = LinuxPanelNative.gdk_display_get_default();
            // The previous window may have closed while the panel was open. Let X11
            // revert focus normally in that case instead of treating BadWindow as fatal.
            LinuxPanelNative.gdk_x11_display_error_trap_push(display);
            LinuxPanelNative.XSetInputFocus(LinuxPanelNative.gdk_x11_display_get_xdisplay(display), _previousFocus, 2, 0);
            LinuxPanelNative.gdk_x11_display_error_trap_pop_ignored(display);
        }
    }

    internal void Update(TrayViewState state)
    {
        if (_panel.Update(state) && Visible)
        {
            Layout();
        }
    }

    internal void Apply(PanelResponse response)
    {
        if (response.Redraw && Visible)
        {
            Layout();
        }
        if (response.Action is { } action)
        {
            _execute(action);
        }
    }

    internal void ShowSettings(PanelSettings settings) => Apply(_panel.ShowSettings(settings));

    internal void UpdateSettings(PanelSettings settings) => Apply(_panel.UpdateSettings(settings));

    internal void ConfirmRemoveMissing(AppHostMenuItem host) => Apply(_panel.ConfirmRemoveMissing(host));

    private void EnsureSurfaces()
    {
        var display = LinuxPanelNative.gdk_display_get_default();
        var monitors = Enumerable.Range(0, LinuxPanelNative.gdk_display_get_n_monitors(display))
            .Select(index =>
            {
                var monitor = LinuxPanelNative.gdk_display_get_monitor(display, index);
                LinuxPanelNative.gdk_monitor_get_geometry(monitor, out var geometry);
                return (Monitor: monitor, Geometry: new PanelRect(geometry.X, geometry.Y, geometry.Width, geometry.Height));
            }).ToArray();
        if (monitors.Length == _surfaces.Count && monitors.Zip(_surfaces).All(pair =>
            pair.First.Monitor == pair.Second.Monitor && pair.First.Geometry == pair.Second.Geometry
            && !pair.Second.Destroyed && pair.Second.Main == (pair.First.Monitor == _monitor)))
        {
            return;
        }
        Hide();
        DestroySurfaces();
        if (!monitors.Any(monitor => monitor.Monitor == _monitor))
        {
            throw new InvalidOperationException("The desktop did not provide an output for the Aspire panel.");
        }
        foreach (var monitor in monitors)
        {
            var surface = CreateSurface(monitor.Monitor, monitor.Geometry, monitor.Monitor == _monitor);
            _surfaces.Add(surface);
            if (surface.Main)
            {
                _window = surface.Window;
                _area = surface.Area;
            }
        }
        var main = _surfaces.Single(surface => surface.Main);
        _accessibility = new(main.Fixed, Apply, _panel.Invoke, ReportFailure,
            widget => ConnectInput(widget, main), widget => _eventSurfaces.Remove(widget));
        if (_logo == 0)
        {
            _logo = Gtk.cairo_image_surface_create_from_png(Path.Combine(AppContext.BaseDirectory, "Aspire.png"));
            if (Gtk.cairo_surface_status(_logo) != 0)
            {
                throw new InvalidOperationException("Could not read the Aspire panel logo.");
            }
        }
    }

    private unsafe Surface CreateSurface(nint monitor, PanelRect geometry, bool main)
    {
        var window = LinuxPanelNative.gtk_window_new(0);
        if (window == 0)
        {
            throw new InvalidOperationException("Could not create the Aspire panel's popup surface.");
        }
        Gtk.g_object_ref_sink(window);
        nint retainedMonitor = 0;
        try
        {
            var area = LinuxPanelNative.gtk_drawing_area_new();
            var container = LinuxPanelNative.gtk_fixed_new();
            retainedMonitor = Gtk.g_object_ref_sink(monitor);
            var surface = new Surface(window, area, container, retainedMonitor, geometry, main);
            Gtk.gtk_window_set_title(window, "Aspire");
            LinuxPanelNative.gtk_window_set_decorated(window, 0);
            LinuxPanelNative.gtk_window_set_resizable(window, 0);
            LinuxPanelNative.gtk_window_set_accept_focus(window, main ? 1 : 0);
            LinuxPanelNative.gtk_window_set_skip_taskbar_hint(window, 1);
            LinuxPanelNative.gtk_window_set_skip_pager_hint(window, 1);
            var visual = LinuxPanelNative.gdk_screen_get_rgba_visual(LinuxPanelNative.gtk_widget_get_screen(window));
            if (visual == 0)
            {
                throw new InvalidOperationException("The desktop does not support transparent Aspire panel surfaces.");
            }
            LinuxPanelNative.gtk_widget_set_visual(window, visual);
            LinuxPanelNative.gtk_widget_set_app_paintable(window, 1);
            LinuxPanelNative.gtk_widget_set_can_focus(area, main ? 1 : 0);
            Gtk.gtk_container_add(window, container);
            LinuxPanelNative.gtk_fixed_put(container, area, 0, 0);
            LinuxPanelNative.gtk_widget_set_size_request(area, (int)geometry.Width, (int)geometry.Height);

            // One transparent, input-receiving surface per output catches outside
            // clicks, including on another monitor. Only one draws the actual panel.
            // Layer-shell positions these without an unavailable Wayland parent/serial.
            // https://wmww.github.io/gtk-layer-shell/
            if (_backend == LinuxPanelBackend.Wayland)
            {
                LinuxLayerShell.Configure(window, monitor, main);
            }
            else
            {
                LinuxPanelNative.gtk_widget_realize(window);
                LinuxPanelNative.gdk_window_set_override_redirect(LinuxPanelNative.gtk_widget_get_window(window), 1);
                LinuxPanelNative.gtk_window_move(window, (int)geometry.X, (int)geometry.Y);
                LinuxPanelNative.gtk_window_resize(window, (int)geometry.Width, (int)geometry.Height);
            }
            _eventSurfaces.Add(window, surface);
            ConnectInput(area, surface);
            Gtk.g_signal_connect_data(area, "draw", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&Draw,
                GCHandle.ToIntPtr(_handle), 0, 0);
            Gtk.g_signal_connect_data(window, "destroy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Destroyed,
                GCHandle.ToIntPtr(_handle), 0, 0);
            foreach (var signal in new[] { "key-press-event", "delete-event", "grab-broken-event", "focus-out-event", "map-event" })
            {
                Gtk.g_signal_connect_data(window, signal, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&Input,
                    GCHandle.ToIntPtr(_handle), 0, 0);
            }
            ConnectInput(window, surface);
            return surface;
        }
        catch
        {
            Gtk.gtk_widget_destroy(window);
            Gtk.g_object_unref(window);
            if (retainedMonitor != 0)
            {
                Gtk.g_object_unref(retainedMonitor);
            }
            throw;
        }
    }

    private unsafe void ConnectInput(nint widget, Surface surface)
    {
        _eventSurfaces[widget] = surface;
        // Motion, button press/release, key press, leave, scroll and smooth-scroll masks.
        // https://docs.gtk.org/gdk3/flags.EventMask.html
        LinuxPanelNative.gtk_widget_add_events(widget, (1 << 2) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 12) | (1 << 21) | (1 << 23));
        var data = GCHandle.ToIntPtr(_handle);
        foreach (var signal in new[] { "motion-notify-event", "leave-notify-event", "button-press-event", "button-release-event", "scroll-event" })
        {
            Gtk.g_signal_connect_data(widget, signal, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&Input, data, 0, 0);
        }
    }

    private TrayPanelPalette ReadPalette()
    {
        var context = LinuxPanelNative.gtk_widget_get_style_context(_area);
        return LinuxPanelNative.gtk_style_context_lookup_color(context, "theme_bg_color", out var color) != 0
            && (color.Red + color.Green + color.Blue) / 3 >= 0.5
            ? TrayPanelPalette.Light : TrayPanelPalette.Dark;
    }

    private void Position()
    {
        var display = LinuxPanelNative.gdk_display_get_default();
        if (LinuxPanelNative.gdk_display_get_n_monitors(display) == 0)
        {
            throw new InvalidOperationException("The desktop did not provide an output for the Aspire panel.");
        }
        if (!_hasAnchor)
        {
            var primary = LinuxPanelNative.gdk_display_get_primary_monitor(display);
            if (primary == 0)
            {
                primary = LinuxPanelNative.gdk_display_get_monitor(display, 0);
            }
            LinuxPanelNative.gdk_monitor_get_workarea(primary, out var initial);
            _anchorX = initial.X + initial.Width - 20;
            _anchorY = initial.Y;
            _hasAnchor = true;
        }
        _monitor = LinuxPanelNative.gdk_display_get_monitor_at_point(display, _anchorX, _anchorY);
        LinuxPanelNative.gdk_monitor_get_workarea(_monitor, out var workarea);
        _workarea = new(workarea.X, workarea.Y, workarea.Width, workarea.Height);
        if (workarea.Width < TrayPanel.Width + 8 || workarea.Height < TrayPanel.HeaderHeight + TrayPanel.FooterHeight + TrayPanel.RowHeight + 16)
        {
            throw new InvalidOperationException("The output is too small to display the Aspire panel.");
        }
        _panel.MaxHeight = Math.Min(TrayPanel.DefaultMaxHeight, workarea.Height - 16);
    }

    internal static PanelRect Place(PanelRect workarea, double anchorX, double anchorY, double height)
    {
        var left = workarea.X + 4;
        var right = Math.Max(left, workarea.Right - TrayPanel.Width - 4);
        var x = Math.Clamp(anchorX - TrayPanel.Width / 2, left, right);
        var y = anchorY >= workarea.Bottom ? workarea.Bottom - height - 4 : Math.Max(workarea.Y + 4, anchorY + 4);
        y = Math.Clamp(y, workarea.Y + 4, Math.Max(workarea.Y + 4, workarea.Bottom - height - 4));
        return new(x, y, TrayPanel.Width, height);
    }

    private void Layout()
    {
        var height = (int)Math.Ceiling(_panel.Scene.Height);
        _bounds = Place(_workarea, _anchorX, _anchorY, height);
        var surface = _surfaces.Single(surface => surface.Main);
        _accessibility!.Update(_panel.Scene, _bounds.X - surface.Geometry.X, _bounds.Y - surface.Geometry.Y, _panel.FocusedTarget);
        LinuxPanelNative.gtk_widget_queue_draw(_area);
    }

    private int HandleInput(nint widget, nint nativeEvent)
    {
        if (!Visible || !_eventSurfaces.TryGetValue(widget, out var surface))
        {
            return 0;
        }
        LinuxPanelNative.gdk_event_get_coords(nativeEvent, out var x, out var y);
        var eventWidget = LinuxPanelNative.gtk_get_event_widget(nativeEvent);
        if (eventWidget != 0 && LinuxPanelNative.gtk_widget_translate_coordinates(eventWidget, surface.Area,
            (int)Math.Floor(x), (int)Math.Floor(y), out var translatedX, out var translatedY) != 0)
        {
            x = translatedX;
            y = translatedY;
        }
        x -= _bounds.X - surface.Geometry.X;
        y -= _bounds.Y - surface.Geometry.Y;
        switch (LinuxPanelNative.gdk_event_get_event_type(nativeEvent))
        {
            case 14: // GDK_MAP
                // Grabbing before the native window is mapped can fail with
                // GDK_GRAB_NOT_VIEWABLE.
                if (surface.Main && _backend == LinuxPanelBackend.X11 && !_grabbed)
                {
                    GrabInput();
                }
                return 0;
            case 0: // GDK_DELETE
                Hide();
                return 1;
            case 12: // A keyboard grab can synthesize focus-out without moving X11 focus.
                if (_backend == LinuxPanelBackend.X11 && surface.Main)
                {
                    LinuxPanelNative.XGetInputFocus(LinuxPanelNative.gdk_x11_display_get_xdisplay(
                        LinuxPanelNative.gdk_display_get_default()), out var focus, out _);
                    if (!_grabbed || focus == LinuxPanelNative.gdk_x11_window_get_xid(
                        LinuxPanelNative.gtk_widget_get_window(_window)))
                    {
                        return 0;
                    }
                }
                if (surface.Main)
                {
                    Hide(restoreFocus: false);
                }
                return 1;
            case 35: // GDK_GRAB_BROKEN: another popup took ownership.
                if (surface.Main)
                {
                    Hide(restoreFocus: false);
                }
                return 1;
            case 3: // GDK_MOTION_NOTIFY
                Apply(surface.Main ? _panel.PointerMoved(x, y) : _panel.PointerLeft());
                LinuxPanelNative.gtk_widget_set_tooltip_text(_area, _panel.HitTest(x, y)?.Tooltip);
                return 1;
            case 11: // GDK_LEAVE_NOTIFY
                Apply(_panel.PointerLeft());
                return 1;
            case 4 or 7: // GDK_BUTTON_PRESS / GDK_BUTTON_RELEASE
                if (LinuxPanelNative.gdk_event_get_event_type(nativeEvent) == 4
                    && (!surface.Main || !new PanelRect(0, 0, _panel.Scene.Width, _panel.Scene.Height).Contains(x, y)))
                {
                    Hide();
                    return 1;
                }
                LinuxPanelNative.gdk_event_get_button(nativeEvent, out var button);
                if (button != 1)
                {
                    return 0;
                }
                Apply(LinuxPanelNative.gdk_event_get_event_type(nativeEvent) == 4 ? _panel.PointerDown(x, y) : _panel.PointerUp(x, y));
                return 1;
            case 31: // GDK_SCROLL
                if (!surface.Main || !new PanelRect(0, 0, _panel.Scene.Width, _panel.Scene.Height).Contains(x, y))
                {
                    return 1;
                }
                if (LinuxPanelNative.gdk_event_get_scroll_deltas(nativeEvent, out _, out var delta) == 0)
                {
                    LinuxPanelNative.gdk_event_get_scroll_direction(nativeEvent, out var direction);
                    delta = direction switch { 0 => -1, 1 => 1, _ => 0 };
                }
                Apply(_panel.Scroll(-delta * 40));
                return 1;
            case 8: // GDK_KEY_PRESS
                LinuxPanelNative.gdk_event_get_keyval(nativeEvent, out var key);
                LinuxPanelNative.gdk_event_get_state(nativeEvent, out var modifiers);
                if (key == ',' && (modifiers & 4) != 0)
                {
                    _execute(new(PanelActionKind.Settings));
                    return 1;
                }
                var mapped = MapKey(key, (modifiers & 1) != 0);
                if (mapped is { } value)
                {
                    Apply(_panel.Key(value));
                    return 1;
                }
                return 0;
            default:
                return 0;
        }
    }

    internal static PanelKey? MapKey(uint key, bool shift) => key switch
    {
        0xFF52 => PanelKey.Up,
        0xFF54 => PanelKey.Down,
        0xFF09 => shift ? PanelKey.ShiftTab : PanelKey.Tab,
        0xFE20 => PanelKey.ShiftTab,
        0xFF0D or 0xFF8D => PanelKey.Enter,
        0x20 => PanelKey.Space,
        0xFF1B => PanelKey.Escape,
        0xFF50 => PanelKey.Home,
        0xFF57 => PanelKey.End,
        _ => null
    };

    private void Render(nint widget, nint context)
    {
        Gtk.cairo_set_operator(context, 0);
        LinuxPanelNative.cairo_paint(context);
        Gtk.cairo_set_operator(context, 2);
        if (!Visible || !_eventSurfaces.TryGetValue(widget, out var surface) || !surface.Main)
        {
            return;
        }
        LinuxPanelNative.cairo_save(context);
        LinuxPanelNative.cairo_translate(context, _bounds.X - surface.Geometry.X, _bounds.Y - surface.Geometry.Y);
        foreach (var command in _panel.Scene.Commands)
        {
            RenderCommand(context, command);
        }
        LinuxPanelNative.cairo_restore(context);
        if (Gtk.cairo_status(context) != 0)
        {
            throw new InvalidOperationException("Could not render the Linux flyout.");
        }
    }

    private void RenderCommand(nint context, PanelCommand command)
    {
        switch (command)
        {
            case FillPathCommand fill:
                SetPath(context, fill.Path);
                SetColor(context, fill.Color);
                Gtk.cairo_fill(context);
                break;
            case StrokePathCommand stroke:
                SetPath(context, stroke.Path);
                SetColor(context, stroke.Color);
                LinuxPanelNative.cairo_set_line_width(context, stroke.Width);
                LinuxPanelNative.cairo_set_line_cap(context, 1);
                LinuxPanelNative.cairo_set_line_join(context, 1);
                LinuxPanelNative.cairo_stroke(context);
                break;
            case TextCommand text:
                RenderText(context, text);
                break;
            case LogoCommand logo:
                LinuxPanelNative.cairo_save(context);
                LinuxPanelNative.cairo_translate(context, logo.Bounds.X, logo.Bounds.Y);
                LinuxPanelNative.cairo_scale(context, logo.Bounds.Width / Gtk.cairo_image_surface_get_width(_logo),
                    logo.Bounds.Height / Gtk.cairo_image_surface_get_height(_logo));
                Gtk.cairo_set_source_surface(context, _logo, 0, 0);
                LinuxPanelNative.cairo_paint(context);
                LinuxPanelNative.cairo_restore(context);
                break;
            case AppIconCommand icon:
                foreach (var fallback in icon.Fallback)
                {
                    RenderCommand(context, fallback);
                }
                break;
            case PushClipCommand clip:
                LinuxPanelNative.cairo_save(context);
                Gtk.cairo_rectangle(context, clip.Bounds.X, clip.Bounds.Y, clip.Bounds.Width, clip.Bounds.Height);
                LinuxPanelNative.cairo_clip(context);
                break;
            case PopClipCommand:
                LinuxPanelNative.cairo_restore(context);
                break;
        }
    }

    private void RenderText(nint context, TextCommand text)
    {
        var layout = LinuxPanelNative.pango_cairo_create_layout(context);
        var font = LinuxPanelNative.pango_font_description_copy(LinuxPanelNative.pango_context_get_font_description(
            LinuxPanelNative.gtk_widget_get_pango_context(_area)));
        try
        {
            LinuxPanelNative.pango_font_description_set_absolute_size(font, PanelFonts.Size(text.Font) * 1024);
            LinuxPanelNative.pango_font_description_set_weight(font, PanelFonts.IsSemibold(text.Font) ? 600 : 400);
            LinuxPanelNative.pango_layout_set_font_description(layout, font);
            LinuxPanelNative.pango_layout_set_text(layout, text.Text, -1);
            LinuxPanelNative.pango_layout_set_width(layout, (int)Math.Round(text.Bounds.Width * 1024));
            LinuxPanelNative.pango_layout_set_height(layout, -text.MaxLines);
            LinuxPanelNative.pango_layout_set_wrap(layout, 2); // PANGO_WRAP_WORD_CHAR
            LinuxPanelNative.pango_layout_set_ellipsize(layout, text.Truncation == PanelTruncation.Middle ? 2 : 3);
            LinuxPanelNative.pango_layout_set_alignment(layout, text.Alignment switch
            {
                PanelTextAlignment.Center => 1,
                PanelTextAlignment.Trailing => 2,
                _ => 0
            });
            var scale = LinuxPanelNative.gtk_widget_get_scale_factor(_area);
            var y = text.MaxLines > 1 ? text.Bounds.Y
                : Math.Round(text.Baseline * scale) / scale - LinuxPanelNative.pango_layout_get_baseline(layout) / 1024d;
            LinuxPanelNative.cairo_save(context);
            Gtk.cairo_rectangle(context, text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height);
            LinuxPanelNative.cairo_clip(context);
            SetColor(context, text.Color);
            LinuxPanelNative.cairo_move_to(context, text.Bounds.X, y);
            LinuxPanelNative.pango_cairo_show_layout(context, layout);
            LinuxPanelNative.cairo_restore(context);
        }
        finally
        {
            LinuxPanelNative.pango_font_description_free(font);
            Gtk.g_object_unref(layout);
        }
    }

    private static void SetColor(nint context, PanelColor color)
        => LinuxPanelNative.cairo_set_source_rgba(context, color.R / 255d, color.G / 255d, color.B / 255d, color.Alpha);

    private static void SetPath(nint context, PanelPath path)
    {
        LinuxPanelNative.cairo_new_path(context);
        foreach (var segment in path.Segments)
        {
            switch (segment.Verb)
            {
                case PanelPathVerb.MoveTo:
                    LinuxPanelNative.cairo_move_to(context, segment.X3, segment.Y3);
                    break;
                case PanelPathVerb.LineTo:
                    LinuxPanelNative.cairo_line_to(context, segment.X3, segment.Y3);
                    break;
                case PanelPathVerb.CubicTo:
                    LinuxPanelNative.cairo_curve_to(context, segment.X1, segment.Y1, segment.X2, segment.Y2, segment.X3, segment.Y3);
                    break;
                case PanelPathVerb.Close:
                    LinuxPanelNative.cairo_close_path(context);
                    break;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Draw(nint widget, nint context, nint data)
        => Dispatch(data, panel => { panel.Render(widget, context); return 1; });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Input(nint widget, nint nativeEvent, nint data)
        => Dispatch(data, panel => panel.HandleInput(widget, nativeEvent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroyed(nint widget, nint data)
        => Dispatch(data, panel =>
        {
            if (panel._eventSurfaces.TryGetValue(widget, out var surface))
            {
                surface.Destroyed = true;
                panel.Hide(restoreFocus: false);
            }
            return 0;
        });

    private static int Dispatch(nint data, Func<LinuxTrayPanel, int> callback)
    {
        var panel = (LinuxTrayPanel)GCHandle.FromIntPtr(data).Target!;
        try
        {
            return panel._disposed ? 0 : callback(panel);
        }
        catch (Exception ex)
        {
            panel.ReportFailure(ex);
            return 1;
        }
    }

    private void ReportFailure(Exception error)
    {
        Console.Error.WriteLine($"Linux flyout failed ({error.GetType().Name}).");
        _controller.ReportActionError(error is InvalidOperationException or FileNotFoundException
            ? error.Message : "The Linux flyout could not complete the requested action.");
        // Never keep an input-catching surface mapped after a native callback
        // fails. The status notice remains available when the panel reopens.
        Hide();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Hide();
        _disposed = true;
        DestroySurfaces();
        if (_logo != 0)
        {
            Gtk.cairo_surface_destroy(_logo);
        }
        _handle.Free();
    }

    private void DestroySurfaces()
    {
        _accessibility?.Dispose();
        _accessibility = null;
        _eventSurfaces.Clear();
        foreach (var surface in _surfaces)
        {
            Gtk.gtk_widget_destroy(surface.Window);
            Gtk.g_object_unref(surface.Window);
            Gtk.g_object_unref(surface.Monitor);
        }
        _surfaces.Clear();
        _window = _area = 0;
    }

    private sealed record Surface(nint Window, nint Area, nint Fixed, nint Monitor, PanelRect Geometry, bool Main)
    {
        public bool Destroyed { get; set; }
    }
}

internal static partial class LinuxPanelNative
{
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_fixed_new();
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_fixed_put(nint container, nint widget, int x, int y);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_fixed_move(nint container, nint widget, int x, int y);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_realize(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_set_accept_focus(nint window, int accept);
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_get_event_widget(nint nativeEvent);
    [LibraryImport("libgtk-3.so.0")] internal static partial int gtk_widget_translate_coordinates(nint source, nint destination, int x, int y, out int translatedX, out int translatedY);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_display_get_n_monitors(nint display);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_display_get_monitor(nint display, int index);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_display_get_primary_monitor(nint display);
    [LibraryImport("libgdk-3.so.0")] internal static partial void gdk_monitor_get_geometry(nint monitor, out Rectangle geometry);
    [LibraryImport("libgdk-3.so.0")] internal static partial void gdk_window_set_override_redirect(nint window, int redirect);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_x11_display_get_xdisplay(nint display);
    [LibraryImport("libgdk-3.so.0")] internal static partial nuint gdk_x11_window_get_xid(nint window);
    [LibraryImport("libgdk-3.so.0")] internal static partial void gdk_x11_display_error_trap_push(nint display);
    [LibraryImport("libgdk-3.so.0")] internal static partial void gdk_x11_display_error_trap_pop_ignored(nint display);
    [LibraryImport("libX11.so.6")] internal static partial int XGetInputFocus(nint display, out nuint focus, out int revert);
    [LibraryImport("libX11.so.6")] internal static partial int XSetInputFocus(nint display, nuint focus, int revert, nuint time);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle
    {
        public int X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rgba
    {
        public double Red, Green, Blue, Alpha;
    }

    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_window_new(int type);
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_drawing_area_new();
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_set_decorated(nint window, int decorated);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_set_resizable(nint window, int resizable);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_set_skip_taskbar_hint(nint window, int skip);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_set_skip_pager_hint(nint window, int skip);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_move(nint window, int x, int y);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_window_resize(nint window, int width, int height);
    [LibraryImport("libgdk-3.so.0")] internal static partial uint gdk_x11_get_server_time(nint window);
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_widget_get_window(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_widget_get_screen(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_set_visual(nint widget, nint visual);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_set_app_paintable(nint widget, int enabled);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_set_can_focus(nint widget, int canFocus);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_add_events(nint widget, int events);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_grab_focus(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_set_size_request(nint widget, int width, int height);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_queue_draw(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_hide(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial int gtk_widget_get_scale_factor(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_widget_get_style_context(nint widget);
    [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_widget_get_pango_context(nint widget);
    [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_widget_set_tooltip_text(nint widget, string? text);
    [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial int gtk_style_context_lookup_color(nint context, string name, out Rgba color);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_screen_get_rgba_visual(nint screen);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_display_get_default();
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_display_get_default_seat(nint display);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_seat_grab(nint seat, nint window, int capabilities, int ownerEvents, nint cursor, nint nativeEvent, nint prepare, nint data);
    [LibraryImport("libgdk-3.so.0")] internal static partial void gdk_seat_ungrab(nint seat);
    [LibraryImport("libgdk-3.so.0")] internal static partial nint gdk_display_get_monitor_at_point(nint display, int x, int y);
    [LibraryImport("libgdk-3.so.0")] internal static partial void gdk_monitor_get_workarea(nint monitor, out Rectangle workarea);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_event_type(nint nativeEvent);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_coords(nint nativeEvent, out double x, out double y);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_button(nint nativeEvent, out uint button);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_keyval(nint nativeEvent, out uint key);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_state(nint nativeEvent, out uint state);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_scroll_deltas(nint nativeEvent, out double x, out double y);
    [LibraryImport("libgdk-3.so.0")] internal static partial int gdk_event_get_scroll_direction(nint nativeEvent, out int direction);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_save(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_restore(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_paint(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_clip(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_new_path(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_close_path(nint context);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_move_to(nint context, double x, double y);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_line_to(nint context, double x, double y);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_curve_to(nint context, double x1, double y1, double x2, double y2, double x3, double y3);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_translate(nint context, double x, double y);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_scale(nint context, double x, double y);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_set_source_rgba(nint context, double red, double green, double blue, double alpha);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_set_line_width(nint context, double width);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_set_line_cap(nint context, int cap);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_set_line_join(nint context, int join);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_stroke(nint context);
    [LibraryImport("libpangocairo-1.0.so.0")] internal static partial nint pango_cairo_create_layout(nint context);
    [LibraryImport("libpangocairo-1.0.so.0")] internal static partial void pango_cairo_show_layout(nint context, nint layout);
    [LibraryImport("libpango-1.0.so.0")] internal static partial nint pango_context_get_font_description(nint context);
    [LibraryImport("libpango-1.0.so.0")] internal static partial nint pango_font_description_copy(nint description);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_font_description_free(nint description);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_font_description_set_absolute_size(nint description, double size);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_font_description_set_weight(nint description, int weight);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_layout_set_font_description(nint layout, nint description);
    [LibraryImport("libpango-1.0.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void pango_layout_set_text(nint layout, string text, int length);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_layout_set_width(nint layout, int width);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_layout_set_height(nint layout, int height);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_layout_set_wrap(nint layout, int wrap);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_layout_set_ellipsize(nint layout, int ellipsize);
    [LibraryImport("libpango-1.0.so.0")] internal static partial void pango_layout_set_alignment(nint layout, int alignment);
    [LibraryImport("libpango-1.0.so.0")] internal static partial int pango_layout_get_baseline(nint layout);
}
