// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Utils;
using Aspire.Shared;

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
    public void ExtractionInProgressMarker_IsDetected()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            BundleService.WriteExtractionInProgressMarker(tempDir.FullName, "13.2.0-dev");

            Assert.True(BundleService.HasExtractionInProgressMarker(tempDir.FullName));
            Assert.False(BundleService.IsExtractionComplete(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsExtractionComplete_ReturnsTrue_WhenVersionMarkerExistsAndNoExtractionInProgressMarker()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            BundleService.WriteVersionMarker(tempDir.FullName, "13.2.0-dev");

            Assert.True(BundleService.IsExtractionComplete(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsUsableExtractedLayout_ReturnsTrue_ForLegacyLayoutWithoutMarkers()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            var managedDirectory = tempDir.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
            tempDir.CreateSubdirectory(BundleDiscovery.DcpDirectoryName);
            File.WriteAllText(Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)), "test");

            Assert.True(BundleService.IsUsableExtractedLayout(tempDir.FullName));
            Assert.False(BundleService.IsExtractionComplete(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsUsableExtractedLayout_ReturnsFalse_WhenVersionMarkerExistsButRequiredContentsAreMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            BundleService.WriteVersionMarker(tempDir.FullName, "13.2.0-dev");

            Assert.False(BundleService.IsUsableExtractedLayout(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CleanLayoutDirectoriesAsync_RemovesExtractionMarkers()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            BundleService.WriteVersionMarker(tempDir.FullName, "13.2.0-dev");
            BundleService.WriteExtractionInProgressMarker(tempDir.FullName, "13.2.0-dev");

            await BundleService.CleanLayoutDirectoriesAsync(tempDir.FullName, CancellationToken.None);

            Assert.Null(BundleService.ReadVersionMarker(tempDir.FullName));
            Assert.False(BundleService.HasExtractionInProgressMarker(tempDir.FullName));
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
    public void GetCurrentVersion_ChangesWhenCliBinaryChanges()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "old");
            File.SetLastWriteTimeUtc(processPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var firstVersion = BundleService.GetCurrentVersion(processPath);

            File.WriteAllText(processPath, "new-content");
            File.SetLastWriteTimeUtc(processPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var secondVersion = BundleService.GetCurrentVersion(processPath);

            Assert.NotEqual(firstVersion, secondVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnsureDirectoryReadyForExtractionAsync_RetriesUntilDirectoryCanBeReplaced()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            var managedPath = tempDir.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName).FullName;
            var attempts = 0;

            await BundleService.EnsureDirectoryReadyForExtractionAsync(
                managedPath,
                CancellationToken.None,
                tryDeleteDirectory: _ => ++attempts < 3
                    ? FileDeleteHelper.DeleteDirectoryResult.Blocked
                    : FileDeleteHelper.DeleteDirectoryResult.Deleted,
                retryDelay: TimeSpan.Zero,
                timeout: TimeSpan.FromSeconds(1));

            Assert.Equal(3, attempts);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnsureDirectoryReadyForExtractionAsync_ThrowsWhenDirectoryRemainsLocked()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            var managedPath = tempDir.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName).FullName;

            var exception = await Assert.ThrowsAsync<IOException>(
                () => BundleService.EnsureDirectoryReadyForExtractionAsync(
                    managedPath,
                    CancellationToken.None,
                    tryDeleteDirectory: _ => FileDeleteHelper.DeleteDirectoryResult.Blocked,
                    retryDelay: TimeSpan.Zero,
                    timeout: TimeSpan.Zero));

            Assert.Contains("still locked by another process", exception.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
