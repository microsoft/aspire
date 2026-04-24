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
    public void GetDefaultExtractDir_ReturnsContainingDirectory()
    {
        // Flat layout: managed/ and dcp/ are siblings of the CLI binary
        if (OperatingSystem.IsWindows())
        {
            var result = BundleService.GetDefaultExtractDir(@"C:\Users\test\.aspire\bin\aspire.exe");
            Assert.Equal(@"C:\Users\test\.aspire\bin", result);
        }
        else
        {
            var result = BundleService.GetDefaultExtractDir("/home/test/.aspire/bin/aspire");
            Assert.Equal("/home/test/.aspire/bin", result);
        }
    }

    [Fact]
    public void GetDefaultExtractDir_WorksForDotnetToolStorePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = BundleService.GetDefaultExtractDir(
                @"C:\Users\test\.dotnet\tools\.store\aspire\13.2.0\aspire\13.2.0\tools\net10.0\win-x64\aspire.exe");
            Assert.Equal(@"C:\Users\test\.dotnet\tools\.store\aspire\13.2.0\aspire\13.2.0\tools\net10.0\win-x64", result);
        }
        else
        {
            var result = BundleService.GetDefaultExtractDir(
                "/home/test/.dotnet/tools/.store/aspire/13.2.0/aspire/13.2.0/tools/net10.0/osx-arm64/aspire");
            Assert.Equal("/home/test/.dotnet/tools/.store/aspire/13.2.0/aspire/13.2.0/tools/net10.0/osx-arm64", result);
        }
    }

    [Fact]
    public void GetDefaultExtractDir_ReturnsNull_ForRootFile()
    {
        if (OperatingSystem.IsWindows())
        {
            // File at the root of a drive has no parent directory
            var result = BundleService.GetDefaultExtractDir(@"aspire.exe");
            // Path.GetDirectoryName returns "" for relative filename with no directory
            Assert.True(result is null or "", $"Expected null or empty but got: {result}");
        }
        else
        {
            var result = BundleService.GetDefaultExtractDir("aspire");
            Assert.True(result is null or "", $"Expected null or empty but got: {result}");
        }
    }

    [Fact]
    public void CleanLayoutDirectories_RemovesExpectedDirs()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            // Create managed/ and dcp/ directories plus a version marker
            var managedDir = Path.Combine(tempDir.FullName, "managed");
            var dcpDir = Path.Combine(tempDir.FullName, "dcp");
            Directory.CreateDirectory(managedDir);
            Directory.CreateDirectory(dcpDir);
            File.WriteAllText(Path.Combine(managedDir, "test.dll"), "test");
            File.WriteAllText(Path.Combine(dcpDir, "dcp.exe"), "test");
            BundleService.WriteVersionMarker(tempDir.FullName, "old-version");

            BundleService.CleanLayoutDirectories(tempDir.FullName);

            Assert.False(Directory.Exists(managedDir));
            Assert.False(Directory.Exists(dcpDir));
            Assert.Null(BundleService.ReadVersionMarker(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CleanLayoutDirectories_PreservesOtherFiles()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");
        try
        {
            // Simulate the CLI binary sitting alongside managed/ and dcp/
            var cliBinary = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(cliBinary, "cli-binary");
            Directory.CreateDirectory(Path.Combine(tempDir.FullName, "managed"));
            Directory.CreateDirectory(Path.Combine(tempDir.FullName, "dcp"));

            BundleService.CleanLayoutDirectories(tempDir.FullName);

            // CLI binary should not be touched
            Assert.True(File.Exists(cliBinary));
        }
        finally
        {
            tempDir.Delete(recursive: true);
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
}
