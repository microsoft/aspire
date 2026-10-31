// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray.Tests.Helpers;

internal static partial class LinuxPngComparer
{
    internal static async Task<CompareResult> Compare(
        Stream received, Stream verified, IReadOnlyDictionary<string, object> _)
    {
        using var directory = new TestTrayStateDirectory();
        var receivedPath = directory.CreateAppHost("received.png");
        var verifiedPath = directory.CreateAppHost("verified.png");
        await using (var output = File.Create(receivedPath))
        {
            await received.CopyToAsync(output);
        }
        await using (var output = File.Create(verifiedPath))
        {
            await verified.CopyToAsync(output);
        }

        return HaveSamePixels(receivedPath, verifiedPath)
            ? CompareResult.Equal : CompareResult.NotEqual("Decoded Cairo pixels differ.");
    }

    internal static bool HaveSamePixels(string received, string verified)
    {
        var actual = Gtk.cairo_image_surface_create_from_png(received);
        var expected = Gtk.cairo_image_surface_create_from_png(verified);
        try
        {
            if (Gtk.cairo_surface_status(actual) != 0 || Gtk.cairo_surface_status(expected) != 0)
            {
                throw new InvalidOperationException("Could not decode the tray artwork for pixel comparison.");
            }
            var width = Gtk.cairo_image_surface_get_width(actual);
            var height = Gtk.cairo_image_surface_get_height(actual);
            if (width != Gtk.cairo_image_surface_get_width(expected)
                || height != Gtk.cairo_image_surface_get_height(expected)
                || cairo_image_surface_get_format(actual) != cairo_image_surface_get_format(expected))
            {
                return false;
            }

            // Cairo decodes PNGs as ARGB32 or RGB24, both with four bytes per pixel.
            // Compare each row without including any stride padding.
            // https://www.cairographics.org/manual/cairo-Image-Surfaces.html
            var actualRow = new byte[checked(width * 4)];
            var expectedRow = new byte[actualRow.Length];
            var actualData = cairo_image_surface_get_data(actual);
            var expectedData = cairo_image_surface_get_data(expected);
            var actualStride = cairo_image_surface_get_stride(actual);
            var expectedStride = cairo_image_surface_get_stride(expected);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(actualData + row * actualStride, actualRow, 0, actualRow.Length);
                Marshal.Copy(expectedData + row * expectedStride, expectedRow, 0, expectedRow.Length);
                if (!actualRow.AsSpan().SequenceEqual(expectedRow))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Gtk.cairo_surface_destroy(actual);
            Gtk.cairo_surface_destroy(expected);
        }
    }

    [LibraryImport("libcairo.so.2")] private static partial int cairo_image_surface_get_format(nint surface);
    [LibraryImport("libcairo.so.2")] private static partial nint cairo_image_surface_get_data(nint surface);
    [LibraryImport("libcairo.so.2")] private static partial int cairo_image_surface_get_stride(nint surface);
}
