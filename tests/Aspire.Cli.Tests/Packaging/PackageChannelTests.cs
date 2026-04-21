// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Tests.Packaging;

public class PackageChannelTests
{
    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
    }

    [Fact]
    public void SourceDetails_ImplicitChannel_ReturnsBasedOnNuGetConfig()
    {
        // Arrange
        var cache = new FakeNuGetPackageCache();

        // Act
        var channel = PackageChannel.CreateImplicitChannel(cache);

        // Assert
        Assert.Equal(PackagingStrings.BasedOnNuGetConfig, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Implicit, channel.Type);
    }

    [Fact]
    public void SourceDetails_ExplicitChannelWithAspireMapping_ReturnsSourceFromMapping()
    {
        // Arrange
        var cache = new FakeNuGetPackageCache();
        var aspireSource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";
        var mappings = new[]
        {
            new PackageMapping("Aspire*", aspireSource),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json")
        };

        // Act
        var channel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease, mappings, cache);

        // Assert
        Assert.Equal(aspireSource, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Explicit, channel.Type);
    }

    [Fact]
    public void SourceDetails_ExplicitChannelWithPrHivePath_ReturnsLocalPath()
    {
        // Arrange
        var cache = new FakeNuGetPackageCache();
        var prHivePath = "/Users/davidfowler/.aspire/hives/pr-10981";
        var mappings = new[]
        {
            new PackageMapping("Aspire*", prHivePath),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json")
        };

        // Act
        var channel = PackageChannel.CreateExplicitChannel("pr-10981", PackageChannelQuality.Prerelease, mappings, cache);

        // Assert
        Assert.Equal(prHivePath, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Explicit, channel.Type);
    }

    [Fact]
    public void SourceDetails_ExplicitChannelWithStagingUrl_ReturnsStagingUrl()
    {
        // Arrange
        var cache = new FakeNuGetPackageCache();
        var stagingUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-48a11dae/nuget/v3/index.json";
        var mappings = new[]
        {
            new PackageMapping("Aspire*", stagingUrl),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json")
        };

        // Act
        var channel = PackageChannel.CreateExplicitChannel("staging", PackageChannelQuality.Stable, mappings, cache, configureGlobalPackagesFolder: true);

        // Assert
        Assert.Equal(stagingUrl, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Explicit, channel.Type);
        Assert.True(channel.ConfigureGlobalPackagesFolder);
    }

    [Fact]
    public void SourceDetails_EmptyMappingsArray_ReturnsBasedOnNuGetConfig()
    {
        // Arrange
        var cache = new FakeNuGetPackageCache();
        var mappings = Array.Empty<PackageMapping>();

        // Act
        var channel = PackageChannel.CreateExplicitChannel("empty", PackageChannelQuality.Stable, mappings, cache);

        // Assert
        Assert.Equal(PackagingStrings.BasedOnNuGetConfig, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Explicit, channel.Type);
    }

    [Fact]
    public void CreateScopedChannelForPackage_PrHiveExpandsToTransitivePackagesInHive()
    {
        var cache = new FakeNuGetPackageCache();
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            CreatePackage(tempDir.FullName, "Aspire.Hosting.Redis", "13.3.0-pr.16125.g5bef2f2f", "Aspire.Hosting");
            CreatePackage(tempDir.FullName, "Aspire.Hosting", "13.3.0-pr.16125.g5bef2f2f");
            CreatePackage(tempDir.FullName, "Aspire.Hosting.AppHost", "13.3.0-pr.16125.g5bef2f2f");

            var mappings = new[]
            {
                new PackageMapping("Aspire*", tempDir.FullName.Replace('\\', '/')),
                new PackageMapping("*", "https://api.nuget.org/v3/index.json")
            };

            var channel = PackageChannel.CreateExplicitChannel("pr-16125", PackageChannelQuality.Prerelease, mappings, cache);

            var scopedChannel = channel.CreateScopedChannelForPackage("Aspire.Hosting.Redis");

            var packageFilters = scopedChannel.Mappings!.Select(mapping => mapping.PackageFilter).ToArray();
            Assert.Contains("Aspire.Hosting.Redis", packageFilters);
            Assert.Contains("Aspire.Hosting", packageFilters);
            Assert.DoesNotContain("Aspire.Hosting.AppHost", packageFilters);
            Assert.Contains("*", packageFilters);
            Assert.DoesNotContain("Aspire*", packageFilters);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateScopedChannelForPackages_PrHiveIncludesExplicitRootPackages()
    {
        var cache = new FakeNuGetPackageCache();
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            CreatePackage(tempDir.FullName, "Aspire.Hosting.Redis", "13.3.0-pr.16125.g5bef2f2f", "Aspire.Hosting");
            CreatePackage(tempDir.FullName, "Aspire.Hosting", "13.3.0-pr.16125.g5bef2f2f");
            CreatePackage(tempDir.FullName, "Aspire.AppHost.Sdk", "13.3.0-pr.16125.g5bef2f2f");
            CreatePackage(tempDir.FullName, "Aspire.Hosting.AppHost", "13.3.0-pr.16125.g5bef2f2f");

            var mappings = new[]
            {
                new PackageMapping("Aspire*", tempDir.FullName.Replace('\\', '/')),
                new PackageMapping("*", "https://api.nuget.org/v3/index.json")
            };

            var channel = PackageChannel.CreateExplicitChannel("pr-16125", PackageChannelQuality.Prerelease, mappings, cache);

            var scopedChannel = channel.CreateScopedChannelForPackages(["Aspire.Hosting.Redis", "Aspire.AppHost.Sdk"]);

            var packageFilters = scopedChannel.Mappings!.Select(mapping => mapping.PackageFilter).ToArray();
            Assert.Contains("Aspire.Hosting.Redis", packageFilters);
            Assert.Contains("Aspire.Hosting", packageFilters);
            Assert.Contains("Aspire.AppHost.Sdk", packageFilters);
            Assert.DoesNotContain("Aspire.Hosting.AppHost", packageFilters);
            Assert.Contains("*", packageFilters);
            Assert.DoesNotContain("Aspire*", packageFilters);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static void CreatePackage(string directory, string packageId, string version, params string[] dependencies)
    {
        var packagePath = Path.Combine(directory, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec");
        using var writer = new StreamWriter(nuspecEntry.Open());

        writer.Write($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>{{version}}</version>
                <dependencies>
            """);

        foreach (var dependency in dependencies)
        {
            writer.Write($$"""
                    <group targetFramework="net10.0">
                      <dependency id="{{dependency}}" version="[{{version}}]" />
                    </group>
                """);
        }

        writer.Write("""
                </dependencies>
              </metadata>
            </package>
            """);
    }
}
