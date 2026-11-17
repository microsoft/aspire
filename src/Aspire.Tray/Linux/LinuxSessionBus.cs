// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aspire.Tray;

/// <summary>
/// One-shot session bus queries made outside the GTK loop by the start helper and the detaching tray.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxSessionBus
{
    private const int NoAutoStart = 1;

    /// <summary>
    /// Reports whether a panel host is registered with the desktop's StatusNotifierWatcher.
    /// </summary>
    internal static bool IsTrayHostRegistered()
    {
        try
        {
            // A missing watcher is an error reply. Do not activate one just to ask.
            // https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/StatusNotifierWatcher/
            var registered = false;
            Call("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties", "Get",
                "('org.kde.StatusNotifierWatcher', 'IsStatusNotifierHostRegistered')", NoAutoStart, 1000, reply =>
                {
                    var boxed = g_variant_get_child_value(reply, 0);
                    try
                    {
                        var value = g_variant_get_variant(boxed);
                        try
                        {
                            registered = g_variant_is_of_type(value, "b") != 0 && g_variant_get_boolean(value) != 0;
                        }
                        finally
                        {
                            Gtk.g_variant_unref(value);
                        }
                    }
                    finally
                    {
                        Gtk.g_variant_unref(boxed);
                    }
                });

            return registered;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            // The tray reported ready, so this should not happen. Without an answer, do not
            // claim a desktop problem that may not exist.
            return true;
        }
    }

    /// <summary>
    /// Moves this process into its own scope when it would otherwise die with a service unit.
    /// </summary>
    /// <remarks>
    /// systemd-xdg-autostart-generator runs the login entry's Exec as a Type=exec service. Before
    /// systemd v250 that unit has no ExitType=cgroup, so it stops when the launcher exits and the
    /// control group, including this detached tray, is killed. Desktop launchers place long-lived
    /// applications in their own scope for the same reason.
    /// https://github.com/systemd/systemd/commit/e83a422797
    /// https://systemd.io/DESKTOP_ENVIRONMENTS/
    /// </remarks>
    internal static void MoveOutOfServiceUnit()
    {
        const string CgroupPath = "/proc/self/cgroup";
        try
        {
            if (!IsInServiceUnit(File.ReadAllText(CgroupPath)))
            {
                return;
            }
            var unit = string.Create(CultureInfo.InvariantCulture, $"app-dev.aspire.tray-{Environment.ProcessId}.scope");
            // StartTransientUnit(name, mode, properties, auxiliary). CollectMode removes a
            // failed scope so that a reused PID cannot collide with its stale unit name.
            // https://www.freedesktop.org/software/systemd/man/latest/org.freedesktop.systemd1.html
            var parameters = string.Create(CultureInfo.InvariantCulture,
                $"('{unit}', 'fail', [('PIDs', <@au [uint32 {Environment.ProcessId}]>), ('CollectMode', <'inactive-or-failed'>), ('Slice', <'app.slice'>)], @a(sa(sv)) [])");
            Call("org.freedesktop.systemd1", "/org/freedesktop/systemd1", "org.freedesktop.systemd1.Manager",
                "StartTransientUnit", parameters, NoAutoStart, 2000, static _ => { });

            // The reply only means the job was queued. The launcher exits once the tray is
            // ready, so wait for the move rather than racing the old unit's shutdown.
            var deadline = Environment.TickCount64 + 2000;
            while (!File.ReadAllText(CgroupPath).Contains(unit, StringComparison.Ordinal))
            {
                if (Environment.TickCount64 >= deadline)
                {
                    Console.Error.WriteLine("Timed out moving the tray to its own systemd scope.");
                    return;
                }
                Thread.Sleep(20);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            // Without a user manager on the session bus there is no unit to outlive, and a
            // failed move leaves the behavior newer systemd already handles. Keep starting.
            Console.Error.WriteLine($"Could not move the tray to its own systemd scope ({ex.GetType().Name}).");
        }
    }

    internal static bool IsInServiceUnit(string cgroup)
    {
        // /proc/self/cgroup has one hierarchy-id:controllers:path line per hierarchy:
        //   0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-aspire@autostart.service
        // A hybrid cgroup v1 host tracks units on name=systemd, and its "0::" line is often "/":
        //   12:cpu,cpuacct:/
        //   1:name=systemd:/user.slice/user-1000.slice/session-4.scope
        //   0::/
        // https://man7.org/linux/man-pages/man7/cgroups.7.html
        return cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 3))
            .Any(fields => fields is [_, "" or "name=systemd", var path] && path.EndsWith(".service", StringComparison.Ordinal));
    }

    private static void Call(string name, string path, string interfaceName, string method, string parameters,
        int flags, int timeout, Action<nint> readReply)
    {
        var connection = g_bus_get_sync(2, 0, out var error); // G_BUS_TYPE_SESSION
        if (connection == 0)
        {
            FreeError(error);
            throw new InvalidOperationException("Could not connect to the session bus.");
        }
        try
        {
            // g_variant_parse avoids the variadic g_variant_new, and returns a non-floating
            // reference, so the call does not consume it and it is released below.
            // https://docs.gtk.org/glib/gvariant-text-format.html
            var arguments = g_variant_parse(0, parameters, 0, 0, out error);
            if (arguments == 0)
            {
                FreeError(error);
                throw new InvalidOperationException("Could not encode the session bus request.");
            }
            try
            {
                var reply = g_dbus_connection_call_sync(connection, name, path, interfaceName, method, arguments, 0, flags, timeout, 0, out error);
                if (reply == 0)
                {
                    FreeError(error);
                    throw new InvalidOperationException("The session bus request failed.");
                }
                try
                {
                    readReply(reply);
                }
                finally
                {
                    Gtk.g_variant_unref(reply);
                }
            }
            finally
            {
                Gtk.g_variant_unref(arguments);
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

    [LibraryImport("libgio-2.0.so.0")] private static partial nint g_bus_get_sync(int busType, nint cancellable, out nint error);
    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_connection_call_sync(nint connection, string name, string path, string interfaceName,
        string method, nint parameters, nint replyType, int flags, int timeout, nint cancellable, out nint error);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_variant_parse(nint type, string text, nint limit, nint endPointer, out nint error);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial int g_variant_is_of_type(nint value, string type);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_get_child_value(nint value, nuint index);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_get_variant(nint value);
    [LibraryImport("libglib-2.0.so.0")] private static partial int g_variant_get_boolean(nint value);
}
