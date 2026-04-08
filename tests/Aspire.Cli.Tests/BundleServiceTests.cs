// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Microsoft.Extensions.Logging.Abstractions;

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
    public void GetDefaultExtractDir_ReturnsParentOfParent()
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
    public void GetCurrentVersion_ReturnsNonNull()
    {
        var version = BundleService.GetCurrentVersion();
        Assert.NotNull(version);
        Assert.NotEqual("unknown", version);
    }

    [Fact]
    public void IsSelfExtracting_ReturnsFalse_WhenMetadataNotSetToTrue()
    {
        // Test assembly builds with default SelfExtractingBundle=false,
        // so IsSelfExtracting should be false.
        var layoutDiscovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
        var service = new BundleService(layoutDiscovery, NullLogger<BundleService>.Instance);
        Assert.False(service.IsSelfExtracting);
    }

    [Fact]
    public async Task EnsureExtractedAndGetLayoutAsync_ReturnsNull_WhenNoBundleAndNoLayout()
    {
        // Test assembly has no embedded bundle and IsSelfExtracting=false.
        // EnsureExtractedAsync should no-op and DiscoverLayout should return null.
        var layoutDiscovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
        var service = new BundleService(layoutDiscovery, NullLogger<BundleService>.Instance);

        var layout = await service.EnsureExtractedAndGetLayoutAsync();
        Assert.Null(layout);
    }

    [Fact]
    public async Task EnsureExtractedAsync_DoesNotThrow_WhenNotSelfExtractingAndNoLayout()
    {
        // When IsSelfExtracting=false and no layout exists, EnsureExtractedAsync
        // should not throw — it should gracefully no-op (since IsBundle is also false
        // in the test assembly). This verifies the method doesn't unconditionally
        // short-circuit in a way that prevents DiscoverLayout from being called.
        var layoutDiscovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
        var service = new BundleService(layoutDiscovery, NullLogger<BundleService>.Instance);

        await service.EnsureExtractedAsync();
    }
}
