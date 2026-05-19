// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Packaging;

public class PackageChannelTests
{
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
    public async Task SearchPackagesAsync_IgnoresPackagesWithInvalidVersions()
    {
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, _, _, prerelease, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(prerelease
                ? [CreatePackage("Contoso.Hosting.MongoDB", "1.0.0-preview.1"), CreatePackage("Contoso.Hosting.InvalidPreview", "preview-build")]
                : [CreatePackage("Contoso.Hosting.Redis", "1.0.0"), CreatePackage("Contoso.Hosting.InvalidStable", "not-a-version")])
        };
        var channel = PackageChannel.CreateImplicitChannel(cache);

        var packages = (await channel.SearchPackagesAsync("hosting", new DirectoryInfo(Environment.CurrentDirectory), static _ => true, CancellationToken.None)).ToArray();

        Assert.Collection(
            packages,
            package => Assert.Equal("1.0.0", package.Version),
            package => Assert.Equal("1.0.0-preview.1", package.Version));
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_IgnoresPackagesWithInvalidVersions()
    {
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, prerelease, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(prerelease
                ? [CreatePackage("Contoso.Hosting.MongoDB", "1.0.0-preview.1"), CreatePackage("Contoso.Hosting.InvalidPreview", "preview-build")]
                : [CreatePackage("Contoso.Hosting.Redis", "1.0.0"), CreatePackage("Contoso.Hosting.InvalidStable", "not-a-version")])
        };
        var channel = PackageChannel.CreateImplicitChannel(cache);

        var packages = (await channel.GetIntegrationPackagesAsync(new DirectoryInfo(Environment.CurrentDirectory), CancellationToken.None)).ToArray();

        Assert.Collection(
            packages,
            package => Assert.Equal("1.0.0", package.Version),
            package => Assert.Equal("1.0.0-preview.1", package.Version));
    }

    private static NuGetPackage CreatePackage(string id, string version)
    {
        return new NuGetPackage
        {
            Id = id,
            Version = version,
            Source = "test"
        };
    }
}
