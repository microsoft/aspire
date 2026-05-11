// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Packaging;

public class PackageChannelTests(ITestOutputHelper outputHelper)
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
    public async Task GetIntegrationPackagesAsync_WithPinnedLocalSource_ReturnsOnlyPinnedLocalPackages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        const string pinnedVersion = "13.4.0-pr.16820.gabcdef";

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Redis.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.PostgreSQL.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.SqlServer.13.3.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.ProjectTemplates.{pinnedVersion}.nupkg"), string.Empty);

        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Local package sources should be enumerated directly.")
        };
        var packageSource = packagesDirectory.FullName.Replace('\\', '/');
        var mappings = new[]
        {
            new PackageMapping("Aspire*", packageSource),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel("local", PackageChannelQuality.Both, mappings, cache, pinnedVersion: pinnedVersion);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Aspire.Hosting.PostgreSQL", package.Id);
                Assert.Equal(pinnedVersion, package.Version);
                Assert.Equal(packageSource, package.Source);
            },
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal(pinnedVersion, package.Version);
                Assert.Equal(packageSource, package.Source);
            });
    }
}
