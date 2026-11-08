// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>
/// Reveals source files using the desktop's standard file-manager service.
/// </summary>
internal static partial class LinuxFileManager
{
    internal static void ShowItem(string path)
    {
        path = TrayAppHostPath.RequireExistingFile(path);
        var connection = g_bus_get_sync(2, 0, out var error);
        if (connection == 0)
        {
            FreeError(error);
            throw new InvalidOperationException("Could not connect to the desktop's file-manager service.");
        }
        try
        {
            // ShowItems takes (as URIs, s StartupId). Build typed variants rather
            // than interpolating paths into D-Bus text or a shell command.
            // https://www.freedesktop.org/wiki/Specifications/file-manager-interface/
            var parameters = CreateParameters(path);
            try
            {
                var reply = g_dbus_connection_call_sync(connection, "org.freedesktop.FileManager1",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1", "ShowItems",
                    parameters, 0, 0, 3000, 0, out error);
                if (reply == 0)
                {
                    FreeError(error);
                    throw new InvalidOperationException("The desktop could not reveal the AppHost file. A file manager supporting org.freedesktop.FileManager1 (such as Files or Dolphin) is required.");
                }
                Gtk.g_variant_unref(reply);
            }
            finally
            {
                Gtk.g_variant_unref(parameters);
            }
        }
        finally
        {
            Gtk.g_object_unref(connection);
        }
    }

    internal static unsafe nint CreateParameters(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        nint* strings = stackalloc nint[1];
        strings[0] = g_variant_new_string(uri);
        var uris = g_variant_new_array("s", strings, 1);
        nint* arguments = stackalloc nint[2];
        arguments[0] = uris;
        arguments[1] = g_variant_new_string("");
        return g_variant_ref_sink(g_variant_new_tuple(arguments, 2));
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
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial nint g_variant_new_string(string value);
    [LibraryImport("libglib-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)] private static unsafe partial nint g_variant_new_array(string type, nint* children, nuint count);
    [LibraryImport("libglib-2.0.so.0")] private static unsafe partial nint g_variant_new_tuple(nint* children, nuint count);
    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_ref_sink(nint value);
}
