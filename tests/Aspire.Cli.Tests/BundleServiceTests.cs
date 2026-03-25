// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;

namespace Aspire.Cli.Tests;

public class BundleServiceTests
{
    [Fact]
    public void IsBundle_ReturnsFalse_WhenNoEmbeddedResource()
    {
        // Test assembly has no embedded bundle.tar.gz resource — verify via OpenPayload
        Assert.Null(BundleService.OpenPayload());
    }

    [Fact]
    public void OpenPayload_ReturnsNull_WhenNoEmbeddedResource()
    {
        Assert.Null(BundleService.OpenPayload());
    }

    [Fact]
    public void VersionMarker_WriteAndRead_Roundtrips()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            BundleService.WriteVersionMarker(tempDir.FullName, "13.2.0-dev");

            var readVersion = BundleService.ReadVersionMarker(tempDir.FullName);
            Assert.Equal("13.2.0-dev", readVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void VersionMarker_ReturnsNull_WhenMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            var readVersion = BundleService.ReadVersionMarker(tempDir.FullName);
            Assert.Null(readVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetDefaultExtractDir_ReturnsAspireDir_ForStandardLayout()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = BundleService.GetDefaultExtractDir(@"C:\Users\test\.aspire\bin\aspire.exe");
            Assert.Equal(@"C:\Users\test\.aspire", result);
        }
        else
        {
            var result = BundleService.GetDefaultExtractDir("/home/test/.aspire/bin/aspire");
            Assert.Equal("/home/test/.aspire", result);
        }
    }

    [Fact]
    public void GetDefaultExtractDir_FallsBackToWellKnownDir_ForNonStandardLayout()
    {
        var expected = BundleService.GetWellKnownAspireDir();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, BundleService.GetDefaultExtractDir(@"C:\Program Files\WinGet\Links\aspire.exe"));
        }
        else
        {
            Assert.Equal(expected, BundleService.GetDefaultExtractDir("/usr/local/bin/aspire"));
            Assert.Equal(expected, BundleService.GetDefaultExtractDir("/opt/homebrew/bin/aspire"));
        }
    }

    [Fact]
    public void GetDefaultExtractDir_FallsBackToWellKnownDir_ForCustomInstallLocation()
    {
        var expected = BundleService.GetWellKnownAspireDir();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, BundleService.GetDefaultExtractDir(@"D:\tools\aspire\bin\aspire.exe"));
        }
        else
        {
            Assert.Equal(expected, BundleService.GetDefaultExtractDir("/opt/aspire/bin/aspire"));
        }
    }

    [Fact]
    public void GetWellKnownAspireDir_ReturnsExpectedPath()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");
        Assert.Equal(expected, BundleService.GetWellKnownAspireDir());
    }

    [Fact]
    public void GetCurrentVersion_ReturnsNonNull()
    {
        var version = BundleService.GetCurrentVersion();
        Assert.NotNull(version);
        Assert.NotEqual("unknown", version);
    }
}
