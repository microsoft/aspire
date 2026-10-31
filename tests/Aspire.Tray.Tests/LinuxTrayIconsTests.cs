// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class LinuxTrayIconsTests
{
    public static bool SupportsCairo => LinuxNativeTestSupport.IsAvailable("libcairo.so.2");

    [Theory(Skip = "Requires Linux Cairo.", SkipUnless = nameof(SupportsCairo))]
    [InlineData("Running")]
    [InlineData("Attention")]
    [InlineData("Connecting")]
    [InlineData("Unavailable")]
    public async Task StatusArtwork(string status)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "aspire.png");
        using var icons = new LinuxTrayIcons(source);

        var path = icons.GetPath(Enum.Parse<TrayIconStatus>(status));

        Assert.Equal(path, icons.GetPath(Enum.Parse<TrayIconStatus>(status)));
        // Cairo/libpng versions can encode identical pixels with different compression.
        // Keep image snapshots, but compare the decoded pixels without a tolerance.
        await Verifier.VerifyFile(path).UseParameters(status).UseDirectory("Snapshots")
            .UseStreamComparer(LinuxPngComparer.Compare, "png");
    }

    [Fact(Skip = "Requires Linux Cairo.", SkipUnless = nameof(SupportsCairo))]
    public void PixelComparisonIgnoresEncodingButPreservesArtwork()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "aspire.png");
        using var directory = new TestTrayStateDirectory();
        var rewritten = directory.CreateAppHost("rewritten.png");
        var surface = Gtk.cairo_image_surface_create_from_png(source);
        try
        {
            Assert.Equal(0, Gtk.cairo_surface_status(surface));
            Assert.Equal(0, Gtk.cairo_surface_write_to_png(surface, rewritten));
        }
        finally
        {
            Gtk.cairo_surface_destroy(surface);
        }

        Assert.False(File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(rewritten)));
        Assert.True(LinuxPngComparer.HaveSamePixels(source, rewritten));

        using var icons = new LinuxTrayIcons(source);
        Assert.False(LinuxPngComparer.HaveSamePixels(
            icons.GetPath(TrayIconStatus.Running), icons.GetPath(TrayIconStatus.Attention)));
        Assert.False(LinuxPngComparer.HaveSamePixels(
            source, icons.GetPath(TrayIconStatus.Connecting)));
    }

    [Fact(Skip = "Requires Linux Cairo.", SkipUnless = nameof(SupportsCairo))]
    public void PixelComparisonRejectsDifferentDimensions()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "aspire.png");
        using var directory = new TestTrayStateDirectory();
        var resized = directory.CreateAppHost("resized.png");
        var surface = Gtk.cairo_image_surface_create(0, 1, 1);
        try
        {
            Assert.Equal(0, Gtk.cairo_surface_status(surface));
            Assert.Equal(0, Gtk.cairo_surface_write_to_png(surface, resized));
        }
        finally
        {
            Gtk.cairo_surface_destroy(surface);
        }

        Assert.False(LinuxPngComparer.HaveSamePixels(source, resized));
    }

    [Fact(Skip = "Requires Linux Cairo.", SkipUnless = nameof(SupportsCairo))]
    public void ArtworkLivesUntilTheIndicatorIsDisposed()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "aspire.png");
        string rendered;
        string directory;
        using (var icons = new LinuxTrayIcons(source))
        {
            Assert.Equal(source, icons.GetPath(TrayIconStatus.Idle));
            rendered = icons.GetPath(TrayIconStatus.Running);
            directory = Path.GetDirectoryName(rendered)!;
            Assert.True(File.Exists(rendered));
        }

        Assert.False(File.Exists(rendered));
        Assert.False(Directory.Exists(directory));
        Assert.True(File.Exists(source));
    }

    [Fact(Skip = "Requires Linux Cairo.", SkipUnless = nameof(SupportsCairo))]
    public void InvalidArtworkFailsExplicitly()
    {
        using var directory = new TestTrayStateDirectory();
        using var icons = new LinuxTrayIcons(directory.CreateAppHost("invalid.png"));

        var error = Assert.Throws<InvalidOperationException>(() => icons.GetPath(TrayIconStatus.Running));

        Assert.Equal("Could not read the Aspire tray icon.", error.Message);
    }
}
