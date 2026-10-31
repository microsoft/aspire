// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>Exposes the shared scene through real GTK/ATK text and action controls.</summary>
internal sealed partial class LinuxPanelAccessibility(nint container, Action<PanelResponse> apply,
    Func<PanelTargetKey, PanelResponse> invoke, Action<Exception> reportFailure,
    Action<nint> register, Action<nint> unregister) : IDisposable
{
    private readonly Dictionary<PanelAccessibleKey, Control> _controls = [];
    private PanelAccessibleKey[] _order = [];

    internal unsafe void Update(TrayPanelScene scene, double x, double y, PanelTargetKey? focus)
    {
        var keys = scene.Accessibility.Select(element => element.Key).ToArray();
        foreach (var key in _controls.Keys.Except(keys).ToArray())
        {
            Remove(key);
        }
        foreach (var element in scene.Accessibility)
        {
            if (!_controls.TryGetValue(element.Key, out var control))
            {
                var widget = element.Role switch
                {
                    PanelAccessibleRole.Text => Gtk.gtk_label_new(element.Name),
                    PanelAccessibleRole.Switch => Gtk.gtk_check_button_new_with_label(""),
                    _ => gtk_button_new_with_label("")
                };
                // Keep controls valid if a compositor destroys their parent before
                // the panel releases its stable accessibility objects.
                Gtk.g_object_ref_sink(widget);
                control = new(widget, element.Key.Target, apply, invoke, reportFailure);
                _controls.Add(element.Key, control);
                gtk_widget_set_opacity(widget, 0);
                gtk_widget_set_no_show_all(widget, 1);
                if (element.Role == PanelAccessibleRole.Text)
                {
                    gtk_label_set_ellipsize(widget, 3);
                    Gtk.gtk_label_set_max_width_chars(widget, 1);
                }
                else
                {
                    control.Handle = GCHandle.Alloc(control);
                    Gtk.g_signal_connect_data(widget, "clicked",
                        (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Clicked, GCHandle.ToIntPtr(control.Handle), 0, 0);
                }
                // Native controls provide semantics, not a second visual layout.
                // Remove theme minimums so their input bounds match the shared scene.
                var provider = gtk_css_provider_new();
                gtk_css_provider_load_from_data(provider, "* { padding: 0; margin: 0; min-width: 0; min-height: 0; border: 0; }", -1, 0);
                gtk_style_context_add_provider(LinuxPanelNative.gtk_widget_get_style_context(widget), provider, 600);
                Gtk.g_object_unref(provider);
                LinuxPanelNative.gtk_fixed_put(container, widget, 0, 0);
                register(widget);
            }
            var accessible = gtk_widget_get_accessible(control.Widget);
            atk_object_set_name(accessible, element.Name);
            atk_object_set_description(accessible, element.Description ?? "");
            Gtk.gtk_widget_set_sensitive(control.Widget, element.Enabled ? 1 : 0);
            if (element.Role == PanelAccessibleRole.Switch)
            {
                control.Updating = true;
                try
                {
                    LinuxDesktop.gtk_toggle_button_set_active(control.Widget, element.Checked == true ? 1 : 0);
                }
                finally
                {
                    control.Updating = false;
                }
            }
            else if (element.Role == PanelAccessibleRole.Text)
            {
                gtk_label_set_text(control.Widget, element.Name);
            }
            var bounds = element.VisibleBounds;
            LinuxPanelNative.gtk_fixed_move(container, control.Widget, (int)Math.Round(x + bounds.X), (int)Math.Round(y + bounds.Y));
            LinuxPanelNative.gtk_widget_set_size_request(control.Widget, Math.Max(1, (int)Math.Ceiling(bounds.Width)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height)));
            if (element.IsOffscreen)
            {
                LinuxPanelNative.gtk_widget_hide(control.Widget);
            }
            else
            {
                gtk_widget_show(control.Widget);
            }
        }
        if (!_order.SequenceEqual(keys))
        {
            // GtkFixed exposes children in insertion order. Reinsert the same native
            // objects only when reading order changes, preserving their ATK identities.
            foreach (var key in keys)
            {
                var widget = _controls[key].Widget;
                Gtk.g_object_ref_sink(widget);
                gtk_container_remove(container, widget);
                var bounds = scene.Accessibility.Single(element => element.Key == key).VisibleBounds;
                LinuxPanelNative.gtk_fixed_put(container, widget, (int)Math.Round(x + bounds.X), (int)Math.Round(y + bounds.Y));
                Gtk.g_object_unref(widget);
            }
            _order = keys;
        }
        Focus(focus);
    }

    internal void Focus(PanelTargetKey? target)
    {
        if (target is { } key && _controls.TryGetValue(PanelAccessibleKey.For(key), out var control))
        {
            LinuxPanelNative.gtk_widget_grab_focus(control.Widget);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Clicked(nint widget, nint data)
    {
        var control = (Control)GCHandle.FromIntPtr(data).Target!;
        if (control.Updating)
        {
            return;
        }
        try
        {
            control.Apply(control.Invoke(control.Target!.Value));
        }
        catch (Exception ex)
        {
            // ATK activation is another native entry point; never unwind into C.
            control.ReportFailure(ex);
        }
    }

    private void Remove(PanelAccessibleKey key)
    {
        var control = _controls[key];
        unregister(control.Widget);
        Gtk.gtk_widget_destroy(control.Widget);
        Gtk.g_object_unref(control.Widget);
        if (control.Handle.IsAllocated)
        {
            control.Handle.Free();
        }
        _controls.Remove(key);
    }

    public void Dispose()
    {
        foreach (var key in _controls.Keys.ToArray())
        {
            Remove(key);
        }
    }

    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_widget_set_opacity(nint widget, double opacity);
    [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial nint gtk_button_new_with_label(string label);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_widget_set_no_show_all(nint widget, int noShow);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_widget_show(nint widget);
    [LibraryImport("libgtk-3.so.0")] private static partial nint gtk_widget_get_accessible(nint widget);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_label_set_ellipsize(nint widget, int ellipsize);
    [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_label_set_text(nint widget, string text);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_container_remove(nint container, nint widget);
    [LibraryImport("libgtk-3.so.0")] private static partial nint gtk_css_provider_new();
    [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int gtk_css_provider_load_from_data(nint provider, string css, nint length, nint error);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_style_context_add_provider(nint context, nint provider, uint priority);
    [LibraryImport("libatk-1.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial void atk_object_set_name(nint accessible, string name);
    [LibraryImport("libatk-1.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial void atk_object_set_description(nint accessible, string description);

    private sealed class Control(nint widget, PanelTargetKey? target, Action<PanelResponse> apply,
        Func<PanelTargetKey, PanelResponse> invoke, Action<Exception> reportFailure)
    {
        public nint Widget { get; } = widget;
        public PanelTargetKey? Target { get; } = target;
        public Action<PanelResponse> Apply { get; } = apply;
        public Func<PanelTargetKey, PanelResponse> Invoke { get; } = invoke;
        public GCHandle Handle;
        public Action<Exception> ReportFailure { get; } = reportFailure;
        public bool Updating;
    }
}
