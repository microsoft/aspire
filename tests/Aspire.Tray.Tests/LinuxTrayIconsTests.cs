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
        await Verifier.VerifyFile(path).UseParameters(status).UseDirectory("Snapshots");
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
