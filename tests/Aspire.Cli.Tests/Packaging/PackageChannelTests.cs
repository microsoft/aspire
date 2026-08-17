// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Packaging;

public class PackageChannelTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void SourceDetails_ImplicitChannel_ReturnsBasedOnNuGetConfig()
    {
        // Arrange
        var cache = new FakeNuGetPackageCache();

        // Act
        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);

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
        var channel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures(), NullLogger.Instance);

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
        var channel = PackageChannel.CreateExplicitChannel("pr-10981", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures(), NullLogger.Instance);

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
        var channel = PackageChannel.CreateExplicitChannel("staging", PackageChannelQuality.Stable, mappings, cache, new TestFeatures(), NullLogger.Instance, configureGlobalPackagesFolder: true);

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
        var channel = PackageChannel.CreateExplicitChannel("empty", PackageChannelQuality.Stable, mappings, cache, new TestFeatures(), NullLogger.Instance);

        // Assert
        Assert.Equal(PackagingStrings.BasedOnNuGetConfig, channel.SourceDetails);
        Assert.Equal(PackageChannelType.Explicit, channel.Type);
    }

    [Fact]
    public async Task GetTemplatePackagesAsync_PinnedChannelWithMappingsOverride_UsesOverrideSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string pinnedVersion = "13.5.0-preview.1";
        const string channelSource = "https://channel.example/v3/index.json";
        const string sourceOverride = "https://proxy.example/v3/index.json";
        var channel = PackageChannel.CreateExplicitChannel(
            "staging",
            PackageChannelQuality.Prerelease,
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
            ],
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            NullLogger.Instance,
            pinnedVersion: pinnedVersion);

        var package = Assert.Single(await channel.GetTemplatePackagesAsync(
            workspace.WorkspaceRoot,
            PackageSourceOverrideMappings.CreateForTemplateOperations(sourceOverride),
            CancellationToken.None));

        Assert.Equal(pinnedVersion, package.Version);
        Assert.Equal(sourceOverride, package.Source);
        Assert.Equal(channelSource, channel.SourceDetails);
    }

    [Fact]
    public async Task GetTemplatePackagesAsync_UnpinnedChannelWithLocalMappingsOverride_EnumeratesOverrideDirectory()
    {
        // `aspire new --source <dir>` hands per-call mappings to an unpinned explicit channel. Local
        // directory discovery must follow those mappings, not the channel's own Aspire* mapping,
        // otherwise the override is silently ignored and the channel's directory is listed instead.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var channelPackagesDirectory = workspace.CreateDirectory("channel-packages");
        var overridePackagesDirectory = workspace.CreateDirectory("override-packages");

        File.WriteAllText(Path.Combine(channelPackagesDirectory.FullName, "Aspire.ProjectTemplates.13.4.0-preview.1.nupkg"), string.Empty);

        // Nested layout mirrors a hierarchical local feed: <id lowercased>/<version>/<id>.<version>.nupkg
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(overridePackagesDirectory.FullName, "aspire.projecttemplates", "13.5.0-preview.2"));
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Aspire.ProjectTemplates.13.5.0-preview.2.nupkg"), string.Empty);

        var cache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Local package sources should be enumerated directly.")
        };
        var channelSource = channelPackagesDirectory.FullName.Replace('\\', '/');
        var overrideSource = overridePackagesDirectory.FullName.Replace('\\', '/');
        var channel = PackageChannel.CreateExplicitChannel(
            "daily",
            PackageChannelQuality.Prerelease,
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
            ],
            cache,
            new TestFeatures(),
            NullLogger.Instance);

        var package = Assert.Single(await channel.GetTemplatePackagesAsync(
            workspace.WorkspaceRoot,
            PackageSourceOverrideMappings.CreateForTemplateOperations(overrideSource),
            CancellationToken.None).DefaultTimeout());

        Assert.Equal("Aspire.ProjectTemplates", package.Id);
        Assert.Equal("13.5.0-preview.2", package.Version);
        Assert.Equal(overrideSource, package.Source);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithPinnedLocalSource_ReturnsOnlyPinnedLocalIntegrationPackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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

        // Dropped — internal Aspire framework packages (AppHost, Sdk, Orchestration.*, Testing, Msi, Integration.Analyzers).
        // Orchestration is seeded with a RID-suffixed shape because no bare
        // Aspire.Hosting.Orchestration nupkg is produced by the build; the exclusion is a
        // prefix rule, so one RID variant exercises the rule against a realistic package name
        // (a regression that tightened StartsWith to Equals would leak every .<rid> variant).
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.AppHost.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Sdk.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Orchestration.linux-arm64.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Testing.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Msi.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Integration.Analyzers.{pinnedVersion}.nupkg"), string.Empty);

        // Dropped — deprecated packages enumerated in DeprecatedPackages.
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.Dapr.{pinnedVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.Hosting.GitHub.Models.{pinnedVersion}.nupkg"), string.Empty);
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
        var channel = PackageChannel.CreateExplicitChannel("local", PackageChannelQuality.Both, mappings, cache, new TestFeatures(), NullLogger.Instance, pinnedVersion: pinnedVersion);

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
    public async Task GetPolyglotCompatiblePackageIdsAsync_WithPinnedLocalSource_ReturnsOnlyTaggedIntegrationPackageIds()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        const string pinnedVersion = "13.4.0-pr.16820.gabcdef";

        CreatePackageWithTags(packagesDirectory, "Aspire.Hosting.Redis", pinnedVersion, "aspire integration hosting cache polyglot");
        CreatePackageWithTags(packagesDirectory, "Aspire.Hosting.PostgreSQL", pinnedVersion, "aspire integration hosting database");
        CreatePackageWithTags(packagesDirectory, "Aspire.Hosting.MongoDB", pinnedVersion, "aspire integration hosting notpolyglot polyglotted");
        CreatePackageWithTags(packagesDirectory, "Aspire.ProjectTemplates", pinnedVersion, "aspire templates polyglot");

        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, _, _, _, _, _, _) => throw new InvalidOperationException("Local package sources should be enumerated directly.")
        };
        var packageSource = packagesDirectory.FullName.Replace('\\', '/');
        var mappings = new[]
        {
            new PackageMapping("Aspire*", packageSource),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel("local", PackageChannelQuality.Both, mappings, cache, new TestFeatures(), NullLogger.Instance, pinnedVersion: pinnedVersion);

        var packageIds = (await channel.GetPolyglotCompatiblePackageIdsAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Aspire.Hosting.Redis"], packageIds);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_AndGetPolyglotCompatiblePackageIdsAsync_WithHierarchicalLocalSource_FindsNestedTaggedPackage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        const string version = "13.4.0-pr.16820.gabcdef";

        CreateHierarchicalPackageWithTags(packagesDirectory, "Aspire.Hosting.Redis", version, "aspire integration hosting cache polyglot");

        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Local package sources should be enumerated directly."),
            GetPackagesAsyncCallback = (_, _, _, _, _, _, _) => throw new InvalidOperationException("Local package sources should be enumerated directly.")
        };
        var packageSource = packagesDirectory.FullName.Replace('\\', '/');
        var mappings = new[]
        {
            new PackageMapping("Aspire*", packageSource),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel("local", PackageChannelQuality.Both, mappings, cache, new TestFeatures(), NullLogger.Instance);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();
        var packageIds = await channel.GetPolyglotCompatiblePackageIdsAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.Redis", package.Id);
        Assert.Equal(version, package.Version);
        Assert.Equal(packageSource, package.Source);
        Assert.Equal(["Aspire.Hosting.Redis"], packageIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithStableRemoteSource_QueriesOnlyStablePackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stableSearches = 0;
        var prereleaseSearches = 0;
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, prerelease, _, _) =>
            {
                if (prerelease)
                {
                    Interlocked.Increment(ref prereleaseSearches);
                    return Task.FromResult<IEnumerable<NuGetPackage>>(
                    [
                        new() { Id = "Aspire.Hosting.Azure.Kubernetes", Version = "13.5.0-preview.1" },
                        new() { Id = "Aspire.Hosting.Redis", Version = "13.6.0-preview.1" }
                    ]);
                }

                Interlocked.Increment(ref stableSearches);
                return Task.FromResult<IEnumerable<NuGetPackage>>(
                [
                    new() { Id = "Aspire.Hosting.Redis", Version = "13.5.0" }
                ]);
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable,
            PackageChannelQuality.Stable,
            [new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)],
            cache,
            new TestFeatures(),
            NullLogger.Instance);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.Equal(1, stableSearches);
        Assert.Equal(0, prereleaseSearches);
        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.Redis", package.Id);
        Assert.Equal("13.5.0", package.Version);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithPrereleaseRemoteSource_QueriesOnlyPrereleaseAndFiltersStableNoise()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stableSearches = 0;
        var prereleaseSearches = 0;
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, prerelease, _, _) =>
            {
                if (!prerelease)
                {
                    Interlocked.Increment(ref stableSearches);
                    return Task.FromResult<IEnumerable<NuGetPackage>>([]);
                }

                Interlocked.Increment(ref prereleaseSearches);
                return Task.FromResult<IEnumerable<NuGetPackage>>(
                [
                    new() { Id = "Aspire.Hosting.Redis", Version = "13.5.0" },
                    new() { Id = "Aspire.Hosting.Redis", Version = "13.6.0-preview.1" }
                ]);
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Daily,
            PackageChannelQuality.Prerelease,
            [new PackageMapping("Aspire*", "https://daily.example/v3/index.json")],
            cache,
            new TestFeatures(),
            NullLogger.Instance);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.Equal(0, stableSearches);
        Assert.Equal(1, prereleaseSearches);
        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.Redis", package.Id);
        Assert.Equal("13.6.0-preview.1", package.Version);
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithBothRemoteSource_ReturnsStableAndPrereleasePackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stableSearches = 0;
        var prereleaseSearches = 0;
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, prerelease, _, _) =>
            {
                if (prerelease)
                {
                    Interlocked.Increment(ref prereleaseSearches);
                    return Task.FromResult<IEnumerable<NuGetPackage>>(
                    [
                        new() { Id = "Aspire.Hosting.Azure.Kubernetes", Version = "13.6.0-pr.19404.gf51e8e1d" },
                        new() { Id = "Aspire.Hosting.Redis", Version = "13.5.0" },
                        new() { Id = "Aspire.Hosting.Redis", Version = "13.6.0-preview.1" }
                    ]);
                }

                Interlocked.Increment(ref stableSearches);
                return Task.FromResult<IEnumerable<NuGetPackage>>(
                [
                    new() { Id = "Aspire.Hosting.Redis", Version = "13.5.0" }
                ]);
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "pr-19404",
            PackageChannelQuality.Both,
            [new PackageMapping("Aspire*", "https://pr.example/v3/index.json")],
            cache,
            new TestFeatures(),
            NullLogger.Instance);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout())
            .OrderBy(package => package.Id, StringComparer.Ordinal)
            .ThenBy(package => package.Version, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(1, stableSearches);
        Assert.Equal(1, prereleaseSearches);
        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Aspire.Hosting.Azure.Kubernetes", package.Id);
                Assert.Equal("13.6.0-pr.19404.gf51e8e1d", package.Version);
            },
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal("13.5.0", package.Version);
            },
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal("13.6.0-preview.1", package.Version);
            });
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_WithStableLocalSource_ReturnsOnlyStablePackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
    public async Task GetIntegrationPackagesAsync_WithBothLocalSource_ReturnsLatestPackageAcrossQualities()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Redis.13.5.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Redis.13.6.0-preview.1.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Azure.Kubernetes.13.6.0-pr.19404.gf51e8e1d.nupkg"), string.Empty);

        var channel = CreateLocalChannel(packagesDirectory, PackageChannelQuality.Both);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Aspire.Hosting.Azure.Kubernetes", package.Id);
                Assert.Equal("13.6.0-pr.19404.gf51e8e1d", package.Version);
            },
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal("13.6.0-preview.1", package.Version);
            });
    }

    [Fact]
    public async Task GetIntegrationPackagesAsync_LocalFolderSource_FiltersDeprecatedByDefault()
    {
        // Mirrors the feed-based behavior in NuGetPackageCache: when the
        // ShowDeprecatedPackages feature flag is off (the default), deprecated
        // integration package ids must be hidden from local-hive / PR-hive listings.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Dapr.13.4.0.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.Hosting.Sql.13.4.0.nupkg"), string.Empty);

        var features = new TestFeatures().SetFeature(KnownFeatures.ShowDeprecatedPackages, true);
        var channel = CreateLocalChannel(packagesDirectory, PackageChannelQuality.Stable, features);

        var packages = (await channel.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout()).ToArray();

        Assert.Contains(packages, p => string.Equals(p.Id, "Aspire.Hosting.Dapr", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packages, p => string.Equals(p.Id, "Aspire.Hosting.Sql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShouldCreateNuGetConfig_StableChannel_ReturnsFalse()
    {
        // A real stable channel still carries mappings (everything -> nuget.org), so the
        // exclusion must come from the channel name, not from an absence of mappings. This
        // guards against scaffolding dropping a redundant <clear/>-based NuGet.config that
        // would wipe the user's ambient feeds.
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.False(channel.ShouldPersistChannelName());
        Assert.False(channel.ShouldCreateNuGetConfig());
    }

    [Fact]
    public void ShouldCreateNuGetConfig_DailyChannel_ReturnsTrue()
    {
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json"),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.True(channel.ShouldPersistChannelName());
        Assert.True(channel.ShouldCreateNuGetConfig());
    }

    [Fact]
    public void ShouldCreateNuGetConfig_StagingChannel_ReturnsTrue()
    {
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abc1234/nuget/v3/index.json"),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel("staging", PackageChannelQuality.Stable, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.True(channel.ShouldPersistChannelName());
        Assert.True(channel.ShouldCreateNuGetConfig());
    }

    [Fact]
    public void ShouldCreateNuGetConfig_PrChannel_ReturnsTrue()
    {
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "/tmp/pr-hive/12345"),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel("pr-12345", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.True(channel.ShouldPersistChannelName());
        Assert.True(channel.ShouldCreateNuGetConfig());
    }

    [Fact]
    public void ShouldCreateNuGetConfig_ImplicitChannel_ReturnsFalse()
    {
        var cache = new FakeNuGetPackageCache();

        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);

        Assert.False(channel.ShouldPersistChannelName());
        Assert.False(channel.ShouldCreateNuGetConfig());
    }

    [Fact]
    public void ShouldCreateNuGetConfig_ExplicitChannelWithoutMappings_ReturnsFalse()
    {
        // An Explicit channel constructed with an empty mappings array has no custom feed to
        // pin, so there is nothing to write into a NuGet.config even though the name would
        // otherwise be persisted.
        var cache = new FakeNuGetPackageCache();

        var channel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease, [], cache, new TestFeatures(), NullLogger.Instance);

        Assert.True(channel.ShouldPersistChannelName());
        Assert.False(channel.ShouldCreateNuGetConfig());
    }

    [Fact]
    public void IsBackedByLocalPackageDirectory_StableNamedChannelMappedToLocalDirectory_ReturnsTrue()
    {
        // This is the ASPIRE_CLI_PACKAGES emulation shape: a locally built CLI emulating a
        // released build synthesizes a channel NAMED after the emulated identity (here "stable")
        // whose Aspire* mapping points at a local directory of .nupkg files. The name-based
        // VersionHelper.IsLocalBuildChannel("stable") would call this remote, so resolution must
        // instead recognize the local directory from the mapping.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping("Aspire*", packagesDirectory.FullName.Replace('\\', '/')),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Both, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.True(channel.IsBackedByLocalPackageDirectory);
    }

    [Fact]
    public void IsBackedByLocalPackageDirectory_FileUriAspireMapping_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping("Aspire*", new Uri(packagesDirectory.FullName).AbsoluteUri),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel("pr-12345", PackageChannelQuality.Both, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.True(channel.IsBackedByLocalPackageDirectory);
    }

    [Fact]
    public void IsBackedByLocalPackageDirectory_RemoteStableChannel_ReturnsFalse()
    {
        // A real stable channel maps everything to nuget.org, so it is not locally backed.
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.False(channel.IsBackedByLocalPackageDirectory);
    }

    [Fact]
    public void IsBackedByLocalPackageDirectory_RemoteDailyFeed_ReturnsFalse()
    {
        // Daily routes Aspire* to an http(s) feed; an http source is never a local directory.
        var cache = new FakeNuGetPackageCache();
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json"),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.False(channel.IsBackedByLocalPackageDirectory);
    }

    [Fact]
    public void IsBackedByLocalPackageDirectory_AspireMappingToNonExistentDirectory_ReturnsFalse()
    {
        // Guard against a stale override: if the mapped directory no longer exists, do not claim
        // the channel can resolve Aspire packages locally (callers would otherwise skip the feed
        // fallback and fail to find any packages).
        var cache = new FakeNuGetPackageCache();
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"aspire-missing-{Guid.NewGuid():N}");
        var mappings = new[]
        {
            new PackageMapping("Aspire*", missingDirectory.Replace('\\', '/')),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Both, mappings, cache, new TestFeatures(), NullLogger.Instance);

        Assert.False(channel.IsBackedByLocalPackageDirectory);
    }

    [Fact]
    public void IsBackedByLocalPackageDirectory_ImplicitChannel_ReturnsFalse()
    {
        var cache = new FakeNuGetPackageCache();

        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);

        Assert.False(channel.IsBackedByLocalPackageDirectory);
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

        return PackageChannel.CreateExplicitChannel("local", quality, mappings, cache, features ?? new TestFeatures(), NullLogger.Instance);
    }

    private static void CreatePackageWithTags(DirectoryInfo packagesDirectory, string packageId, string version, string tags)
    {
        CreatePackageWithTags(Path.Combine(packagesDirectory.FullName, $"{packageId}.{version}.nupkg"), packageId, version, tags);
    }

    private static void CreateHierarchicalPackageWithTags(DirectoryInfo packagesDirectory, string packageId, string version, string tags)
    {
        var packagePath = Path.Combine(packagesDirectory.FullName, packageId.ToLowerInvariant(), version, $"{packageId}.{version}.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        CreatePackageWithTags(packagePath, packageId, version, tags);
    }

    private static void CreatePackageWithTags(string packagePath, string packageId, string version, string tags)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry($"{packageId}.nuspec");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>{version}</version>
                <authors>Aspire</authors>
                <description>Test package</description>
                <tags>{tags}</tags>
              </metadata>
            </package>
            """);
    }
}
