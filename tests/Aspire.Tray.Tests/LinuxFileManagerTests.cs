// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public partial class LinuxFileManagerTests
{
    public static bool SupportsGlib => LinuxNativeTestSupport.IsAvailable("libglib-2.0.so.0");

    [Fact(Skip = "Requires Linux GLib.", SkipUnless = nameof(SupportsGlib))]
    public Task RevealUsesAFileUriAndTypedDbusArguments()
    {
        var path = "/home/dev/project 'quotes' & $(echo); %20 #/apphost.cs";
        var parameters = LinuxFileManager.CreateParameters(path);
        try
        {
            var text = g_variant_print(parameters, 1);
            try
            {
                return Verifier.Verify(Marshal.PtrToStringUTF8(text), "txt").UseDirectory("Snapshots").ToTask();
            }
            finally
            {
                g_free(text);
            }
        }
        finally
        {
            Gtk.g_variant_unref(parameters);
        }
    }

    [Fact]
    public void RevealRequiresAnExistingSourceFileBeforeAccessingDbus()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        File.Delete(path);

        Assert.Throws<FileNotFoundException>(() => LinuxFileManager.ShowItem(path));
    }

    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_variant_print(nint value, int annotate);
    [LibraryImport("libglib-2.0.so.0")] private static partial void g_free(nint value);
}
