// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
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
        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures());

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
        var channel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures());

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
        var channel = PackageChannel.CreateExplicitChannel("pr-10981", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures());

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
        var channel = PackageChannel.CreateExplicitChannel("staging", PackageChannelQuality.Stable, mappings, cache, new TestFeatures(), configureGlobalPackagesFolder: true);

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
        var channel = PackageChannel.CreateExplicitChannel("empty", PackageChannelQuality.Stable, mappings, cache, new TestFeatures());

        // Assert
        Assert.Equal(PackagingStrings.BasedOnNuGetConfig, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Explicit, channel.Type);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithPinnedLocalSource_ReturnsOnlyPinnedLocalIntegrationPackages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        const string pinnedVersion = "13.4.0-pr.16820.gabcdef";

        // Kept — Aspire.Hosting.* / CommunityToolkit.Aspire.Hosting.* integration namespaces.
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Redis.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.PostgreSQL.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"CommunityToolkit.Aspire.Hosting.NodeJS.{pinnedVersion}.nupkg"), string.Empty);

        // Dropped — pinned-version mismatch (otherwise-eligible integration at the wrong version).
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.SqlServer.13.3.0.nupkg"), string.Empty);

        // Dropped — outside the integration namespace.
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.ProjectTemplates.{pinnedVersion}.nupkg"), string.Empty);

        // Dropped — internal Aspire framework packages (AppHost, Sdk, Orchestration.*, Testing, Msi).
        // Orchestration is seeded with a RID-suffixed shape because no bare
        // Aspire.Hosting.Orchestration nupkg is produced by the build; the exclusion is a
        // prefix rule, so one RID variant exercises the rule against a realistic package name
        // (a regression that tightened StartsWith to Equals would leak every .<rid> variant).
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.AppHost.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Sdk.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Orchestration.linux-arm64.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Testing.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Msi.{pinnedVersion}.nupkg"), string.Empty);

        // Dropped — deprecated packages enumerated in DeprecatedPackages.
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Dapr.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.NodeJs.{pinnedVersion}.nupkg"), string.Empty);

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
        var channel = PackageChannel.CreateExplicitChannel("local", PackageChannelQuality.Both, mappings, cache, new TestFeatures(), pinnedVersion: pinnedVersion);

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
            },
            package =>
            {
                Assert.Equal("CommunityToolkit.Aspire.Hosting.NodeJS", package.Id);
                Assert.Equal(pinnedVersion, package.Version);
                Assert.Equal(packageSource, package.Source);
            });
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithStableLocalSource_ReturnsOnlyStablePackages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Redis.13.4.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Redis.13.5.0-preview.1.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.PostgreSQL.13.4.0-preview.1.nupkg"), string.Empty);

        var channel = CreateLocalChannel(packagesDirectory, PackageChannelQuality.Stable);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.Redis", package.Id);
        Assert.Equal("13.4.0", package.Version);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithPrereleaseLocalSource_ReturnsOnlyPrereleasePackages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Redis.13.4.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Redis.13.5.0-preview.1.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.PostgreSQL.13.4.0.nupkg"), string.Empty);

        var channel = CreateLocalChannel(packagesDirectory, PackageChannelQuality.Prerelease);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.Redis", package.Id);
        Assert.Equal("13.5.0-preview.1", package.Version);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_LocalFolderSource_FiltersDeprecatedByDefault()
    {
        // Mirrors the feed-based behavior in NuGetPackageCache: when the
        // ShowDeprecatedPackages feature flag is off (the default), deprecated
        // integration package ids must be hidden from local-hive / PR-hive listings.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Dapr.13.4.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Sql.13.4.0.nupkg"), string.Empty);

        var channel = CreateLocalChannel(packagesDirectory, PackageChannelQuality.Stable);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.DoesNotContain(packages, p => string.Equals(p.Id, "Aspire.Hosting.Dapr", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packages, p => string.Equals(p.Id, "Aspire.Hosting.Sql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_LocalFolderSource_IncludesDeprecatedWhenFlagEnabled()
    {
        // When ShowDeprecatedPackages is enabled, deprecated ids must appear in
        // local-hive listings just as they do on the feed-based path; without this,
        // a user who flipped the flag silently sees nothing change on PR/local hives.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Dapr.13.4.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Sql.13.4.0.nupkg"), string.Empty);

        var features = new TestFeatures().SetFeature(KnownFeatures.ShowDeprecatedPackages, true);
        var channel = CreateLocalChannel(packagesDirectory, PackageChannelQuality.Stable, features);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.Contains(packages, p => string.Equals(p.Id, "Aspire.Hosting.Dapr", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packages, p => string.Equals(p.Id, "Aspire.Hosting.Sql", StringComparison.OrdinalIgnoreCase));
    }

    private static PackageChannel CreateLocalChannel(DirectoryInfo packagesDirectory, PackageChannelQuality quality, IFeatures? features = null)
    {
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

        return PackageChannel.CreateExplicitChannel("local", quality, mappings, cache, features ?? new TestFeatures());
    }
}
