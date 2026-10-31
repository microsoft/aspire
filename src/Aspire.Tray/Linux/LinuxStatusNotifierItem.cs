// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Aspire.Tray;

/// <summary>Exports the native tray icon independently of its application-owned panel.</summary>
internal sealed partial class LinuxStatusNotifierItem : IDisposable
{
    internal const string ObjectPath = "/StatusNotifierItem";
    internal const string InterfaceName = "org.kde.StatusNotifierItem";

    // Unlike AppIndicator's menu-first frontend, ItemIsMenu=false exposes primary
    // activation separately. The host supplies screen coordinates to our window.
    // https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/StatusNotifierItem/
    internal const string InterfaceXml = """
        <node>
          <interface name="org.kde.StatusNotifierItem">
            <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
            <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
            <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
            <method name="Scroll"><arg type="i" direction="in"/><arg type="s" direction="in"/></method>
            <property name="Category" type="s" access="read"/>
            <property name="Id" type="s" access="read"/>
            <property name="Title" type="s" access="read"/>
            <property name="Status" type="s" access="read"/>
            <property name="WindowId" type="u" access="read"/>
            <property name="IconName" type="s" access="read"/>
            <property name="IconPixmap" type="a(iiay)" access="read"/>
            <property name="OverlayIconName" type="s" access="read"/>
            <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
            <property name="AttentionIconName" type="s" access="read"/>
            <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
            <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
            <property name="ItemIsMenu" type="b" access="read"/>
            <property name="Menu" type="o" access="read"/>
            <signal name="NewIcon"/>
            <signal name="NewToolTip"/>
          </interface>
        </node>
        """;

    private readonly nint _watcher;
    private readonly Action<int, int> _activate;
    private readonly nint _connection;
    private readonly nint _node;
    private readonly uint _registration;
    private readonly nint _cancellable;
    private int _pendingRegistrations;
    private string _icon;
    private string _description = "Aspire";
    private string? _registeredOwner;
    private string? _registeringOwner;
    private Exception? _failure;
    private bool _disposed;

    internal unsafe LinuxStatusNotifierItem(nint watcher, string icon, Action<int, int> activate)
    {
        _watcher = watcher;
        _icon = icon;
        _activate = activate;
        try
        {
            _connection = g_bus_get_sync(2, 0, out var error);
            if (_connection == 0)
            {
                FreeError(error);
                throw new InvalidOperationException("Could not connect the tray icon to the session bus.");
            }
            _cancellable = g_cancellable_new();
            _node = g_dbus_node_info_new_for_xml(InterfaceXml, out error);
            if (_node == 0)
            {
                FreeError(error);
                throw new InvalidOperationException("Could not create the tray icon's D-Bus interface.");
            }
            var handle = GCHandle.Alloc(this);
            var table = new InterfaceVTable
            {
                Method = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, void>)&Method,
                Property = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint>)&Property
            };
            // GIO copies the vtable and owns the callback handle until unregistration.
            // https://docs.gtk.org/gio/method.DBusConnection.register_object.html
            _registration = g_dbus_connection_register_object(_connection, ObjectPath,
                g_dbus_node_info_lookup_interface(_node, InterfaceName), &table, GCHandle.ToIntPtr(handle),
                (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Destroy, out error);
            if (_registration == 0)
            {
                handle.Free();
                FreeError(error);
                throw new InvalidOperationException("Could not export the native tray icon.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool IsRegistered => _registeredOwner is not null;

    internal unsafe void PollRegistration()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException("The native tray icon failed.", _failure);
        }
        var owner = GetWatcherOwner();
        if (owner == _registeredOwner)
        {
            return;
        }
        _registeredOwner = null;
        if (owner is null || owner == _registeringOwner)
        {
            return;
        }
        _registeringOwner = owner;
        var request = GCHandle.Alloc(new Registration(this, owner));
        _pendingRegistrations++;
        var parameters = g_variant_ref_sink(Tuple([g_variant_new_string(ObjectPath)]));
        try
        {
            // Registration must be asynchronous: a watcher can inspect our exported
            // properties before replying, and their callbacks run on this GTK context.
            g_dbus_proxy_call(_watcher, "RegisterStatusNotifierItem", parameters, 0, 5000, _cancellable,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Registered, GCHandle.ToIntPtr(request));
        }
        catch
        {
            request.Free();
            _pendingRegistrations--;
            throw;
        }
        finally
        {
            Gtk.g_variant_unref(parameters);
        }
    }

    private string? GetWatcherOwner()
    {
        var value = g_dbus_proxy_get_name_owner(_watcher);
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            g_free(value);
        }
    }

    internal void Update(string icon, string description)
    {
        if (_icon != icon)
        {
            _icon = icon;
            Emit("NewIcon");
        }
        if (_description != description)
        {
            _description = description;
            Emit("NewToolTip");
        }
    }

    private void Emit(string signal)
    {
        if (g_dbus_connection_emit_signal(_connection, 0, ObjectPath, InterfaceName, signal, 0, out var error) == 0)
        {
            FreeError(error);
            throw new InvalidOperationException("Could not update the native tray icon.");
        }
    }

    private nint GetProperty(string name) => name switch
    {
        "Category" => g_variant_new_string("ApplicationStatus"),
        "Id" => g_variant_new_string("dev.aspire.tray"),
        "Title" => g_variant_new_string("Aspire"),
        "Status" => g_variant_new_string("Active"),
        "WindowId" => g_variant_new_uint32(0),
        "IconName" => g_variant_new_string(_icon),
        "OverlayIconName" or "AttentionIconName" => g_variant_new_string(""),
        "IconPixmap" or "OverlayIconPixmap" or "AttentionIconPixmap" => EmptyPixmaps(),
        "ToolTip" => Tuple([g_variant_new_string(_icon), EmptyPixmaps(),
            g_variant_new_string("Aspire"), g_variant_new_string(_description)]),
        "ItemIsMenu" => g_variant_new_boolean(0),
        "Menu" => g_variant_new_object_path("/NO_DBUSMENU"),
        _ => throw new InvalidOperationException("Unknown tray icon property.")
    };

    private static unsafe nint EmptyPixmaps() => g_variant_new_array("(iiay)", null, 0);

    private static unsafe nint Tuple(ReadOnlySpan<nint> children)
    {
        fixed (nint* pointer = children)
        {
            return g_variant_new_tuple(pointer, (nuint)children.Length);
        }
    }

    private static int Coordinate(nint parameters, nuint index)
    {
        var child = g_variant_get_child_value(parameters, index);
        try
        {
            return g_variant_get_int32(child);
        }
        finally
        {
            Gtk.g_variant_unref(child);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Method(nint connection, nint sender, nint path, nint iface, nint method, nint parameters, nint invocation, nint data)
    {
        var item = (LinuxStatusNotifierItem)GCHandle.FromIntPtr(data).Target!;
        try
        {
            var name = Marshal.PtrToStringUTF8(method);
            switch (name)
            {
                case "Activate" or "SecondaryActivate" or "ContextMenu":
                    item._activate(Coordinate(parameters, 0), Coordinate(parameters, 1));
                    break;
                case "Scroll":
                    break;
                default:
                    throw new InvalidOperationException("Unknown tray icon method.");
            }
            g_dbus_method_invocation_return_value(invocation, 0);
        }
        catch (Exception ex)
        {
            item._failure = ex;
            g_dbus_method_invocation_return_dbus_error(invocation, "org.freedesktop.DBus.Error.Failed",
                "The tray could not handle the desktop activation request.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Property(nint connection, nint sender, nint path, nint iface, nint property, nint error, nint data)
    {
        var item = (LinuxStatusNotifierItem)GCHandle.FromIntPtr(data).Target!;
        try
        {
            return item.GetProperty(Marshal.PtrToStringUTF8(property)!);
        }
        catch (Exception ex)
        {
            item._failure = ex;
            g_set_error_literal(error, g_dbus_error_quark(), 0, "The tray icon property could not be read.");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Registered(nint proxy, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var request = (Registration)handle.Target!;
        var item = request.Item;
        try
        {
            var reply = g_dbus_proxy_call_finish(proxy, result, out var error);
            if (reply != 0)
            {
                Gtk.g_variant_unref(reply);
            }
            FreeError(error);
            if (!item._disposed && item.GetWatcherOwner() == request.Owner)
            {
                item._registeringOwner = null;
                if (reply == 0)
                {
                    throw new InvalidOperationException("The StatusNotifierWatcher rejected the native tray icon's registration.");
                }
                item._registeredOwner = request.Owner;
            }
        }
        catch (Exception ex)
        {
            if (!item._disposed)
            {
                item._failure = ex;
            }
        }
        finally
        {
            item._pendingRegistrations--;
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(nint data) => GCHandle.FromIntPtr(data).Free();

    private static void FreeError(nint error)
    {
        if (error != 0)
        {
            LinuxDesktop.g_error_free(error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_cancellable != 0)
        {
            g_cancellable_cancel(_cancellable);
            // Complete canceled callbacks before releasing their UI-context handles.
            // Drain only during shutdown, with a bound if the bus has already failed.
            var started = Stopwatch.GetTimestamp();
            while (_pendingRegistrations > 0 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
            {
                if (g_main_context_iteration(0, 0) == 0)
                {
                    Thread.Sleep(1);
                }
            }
            if (_pendingRegistrations > 0)
            {
                Console.Error.WriteLine("Native tray registration cancellation did not finish before shutdown.");
            }
            Gtk.g_object_unref(_cancellable);
        }
        if (_registration != 0)
        {
            g_dbus_connection_unregister_object(_connection, _registration);
        }
        if (_node != 0)
        {
            g_dbus_node_info_unref(_node);
        }
        if (_connection != 0)
        {
            Gtk.g_object_unref(_connection);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceVTable
    {
        public nint Method, Property, SetProperty;
        public nint Padding0, Padding1, Padding2, Padding3, Padding4, Padding5, Padding6, Padding7;
    }

    [LibraryImport("libgio-2.0.so.0")] private static partial nint g_bus_get_sync(int type, nint cancellable, out nint error);
    [LibraryImport("libgio-2.0.so.0")] private static partial nint g_cancellable_new();
    [LibraryImport("libgio-2.0.so.0")] private static partial void g_cancellable_cancel(nint cancellable);
    [LibraryImport("libgio-2.0.so.0")] private static partial uint g_dbus_error_quark();
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial void g_set_error_literal(nint location, uint domain, int code, string message);
    [LibraryImport("libglib-2.0.so.0")] private static partial int g_main_context_iteration(nint context, int block);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_node_info_new_for_xml(string xml, out nint error);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_node_info_lookup_interface(nint node, string name);
    [LibraryImport("libgio-2.0.so.0")] private static partial void g_dbus_node_info_unref(nint node);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial uint g_dbus_connection_register_object(nint connection, string path, nint info,
        InterfaceVTable* table, nint data, nint destroy, out nint error);
    [LibraryImport("libgio-2.0.so.0")] private static partial int g_dbus_connection_unregister_object(nint connection, uint registration);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void g_dbus_proxy_call(nint proxy, string method, nint parameters, int flags, int timeout,
        nint cancellable, nint callback, nint data);
    [LibraryImport("libgio-2.0.so.0")] private static partial nint g_dbus_proxy_call_finish(nint proxy, nint result, out nint error);
    [LibraryImport("libgio-2.0.so.0")] private static partial nint g_dbus_proxy_get_name_owner(nint proxy);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int g_dbus_connection_emit_signal(nint connection, nint destination, string path,
        string iface, string signal, nint parameters, out nint error);
    [LibraryImport("libgio-2.0.so.0")] private static partial void g_dbus_method_invocation_return_value(nint invocation, nint value);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void g_dbus_method_invocation_return_dbus_error(nint invocation, string name, string message);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial nint g_variant_new_string(string value);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial nint g_variant_new_object_path(string value);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_new_uint32(uint value);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_new_boolean(int value);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static unsafe partial nint g_variant_new_array(string type, nint* children, nuint count);
    [LibraryImport("libglib-2.0.so.0")] private static unsafe partial nint g_variant_new_tuple(nint* children, nuint count);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_ref_sink(nint value);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_get_child_value(nint value, nuint index);
    [LibraryImport("libglib-2.0.so.0")] private static partial int g_variant_get_int32(nint value);
    [LibraryImport("libglib-2.0.so.0")] private static partial void g_free(nint value);

    private sealed record Registration(LinuxStatusNotifierItem Item, string Owner);
}
