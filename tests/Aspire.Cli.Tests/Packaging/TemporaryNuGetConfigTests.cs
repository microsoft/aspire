// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.Packaging;

public class TemporaryNuGetConfigTests
{
    [Fact]
    public async Task CreateAsync_IncludesSourcesAndMappings()
    {
        using var config = await TemporaryNuGetConfig.CreateAsync(
        [
            new PackageMapping("Aspire.*", "https://example.com/feed1"),
            new PackageMapping(PackageMapping.AllPackages, "https://example.com/feed2"),
            new PackageMapping("Microsoft.*", "https://example.com/feed1")
        ]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var sourceKeys = document.Descendants("packageSources")
            .Elements("add")
            .ToDictionary(
                static element => element.Attribute("value")!.Value,
                static element => element.Attribute("key")!.Value,
                PackageSourceIdentity.Comparer);

        Assert.NotNull(document.Descendants("packageSources").ElementAt(0).Element("clear"));
        Assert.Equal(2, sourceKeys.Count);
        Assert.Contains(
            document.Descendants("packageSourceMapping").Elements("packageSource"),
            element => element.Attribute("key")?.Value == sourceKeys["https://example.com/feed1"] &&
                element.Elements("package").Select(static package => package.Attribute("pattern")?.Value)
                    .SequenceEqual(["Aspire.*", "Microsoft.*"]));
        Assert.Contains(
            document.Descendants("packageSourceMapping").Elements("packageSource"),
            element => element.Attribute("key")?.Value == sourceKeys["https://example.com/feed2"] &&
                element.Elements("package").Single().Attribute("pattern")?.Value == PackageMapping.AllPackages);
    }

    [Fact]
    public async Task CreateAsync_WithConfiguredGlobalPackagesFolder_AddsConfigEntry()
    {
        using var config = await TemporaryNuGetConfig.CreateAsync(
            [new PackageMapping("Aspire.*", "https://example.com/feed")],
            configureGlobalPackagesFolder: true,
            globalPackagesFolderValue: "/packages");

        var document = XDocument.Load(config.ConfigFile.FullName);

        Assert.Equal(
            "/packages",
            document.Descendants("config")
                .Elements("add")
                .Single(element => element.Attribute("key")?.Value == "globalPackagesFolder")
                .Attribute("value")?.Value);
    }

    [Fact]
    public async Task CreateAsync_PreservesCaseDistinctSourcePaths()
    {
        using var config = await TemporaryNuGetConfig.CreateAsync(
        [
            new PackageMapping("Upper.*", "https://example.com/Feed/index.json"),
            new PackageMapping("Lower.*", "https://example.com/feed/index.json")
        ]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var sources = document.Descendants("packageSources")
            .Elements("add")
            .Select(static element => element.Attribute("value")!.Value)
            .ToArray();

        Assert.Equal(
        [
            "https://example.com/Feed/index.json",
            "https://example.com/feed/index.json"
        ],
            sources);
    }

    [Fact]
    public async Task CreateRestoreOverlayAsync_ReusesAmbientKeyAndDefinesMissingSource()
    {
        const string channelSource = "https://example.com/aspire";
        const string fallbackSource = "https://example.com/fallback";
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
        [
            new PackageMapping("Aspire*", channelSource),
            new PackageMapping(PackageMapping.AllPackages, fallbackSource)
        ],
            sources:
            [
                new NuGetConfigSource("anonymousAlias", channelSource, IsAmbient: true, IsEnabled: true),
                new NuGetConfigSource("private", channelSource, IsAmbient: true, IsEnabled: true),
                new NuGetConfigSource("aspire-0", fallbackSource, IsAmbient: false, IsEnabled: true)
            ]);

        var document = XDocument.Load(config.ConfigFile.FullName);

        var addedSource = Assert.Single(document.Descendants("packageSources").Elements("add"));
        Assert.Equal("aspire-0", addedSource.Attribute("key")?.Value);
        Assert.Equal(fallbackSource, addedSource.Attribute("value")?.Value);
        var mapping = Assert.Single(document.Descendants("packageSourceMapping"));
        Assert.NotNull(mapping.Element("clear"));
        var patternsByKey = mapping.Elements("packageSource").ToDictionary(
            static source => source.Attribute("key")!.Value,
            static source => source.Element("package")!.Attribute("pattern")!.Value,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Aspire*", patternsByKey["anonymousAlias"]);
        Assert.Equal("Aspire*", patternsByKey["private"]);
        Assert.Equal("Aspire*", patternsByKey[channelSource]);
        Assert.Equal(PackageMapping.AllPackages, patternsByKey["aspire-0"]);
        Assert.Equal(PackageMapping.AllPackages, patternsByKey[fallbackSource]);
    }

    [Fact]
    public async Task CreateRestoreOverlayAsync_EnablesExplicitlySelectedAmbientSource()
    {
        const string source = "https://example.com/aspire";
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            [new PackageMapping("Aspire*", source)],
            sources:
            [
                new NuGetConfigSource("private", source, IsAmbient: true, IsEnabled: false),
                new NuGetConfigSource("disabledAlias", source, IsAmbient: true, IsEnabled: false)
            ]);

        var document = XDocument.Load(config.ConfigFile.FullName);

        Assert.Empty(document.Descendants("packageSources"));
        var disabledPackageSources = Assert.Single(document.Descendants("disabledPackageSources"));
        Assert.NotNull(disabledPackageSources.Element("clear"));
        var disabledSource = Assert.Single(disabledPackageSources.Elements("add"));
        Assert.Equal("disabledAlias", disabledSource.Attribute("key")?.Value);
        Assert.Equal("true", disabledSource.Attribute("value")?.Value);
        Assert.Equal(
            ["private", source],
            document.Descendants("packageSourceMapping")
                .Elements("packageSource")
                .Select(static mapping => mapping.Attribute("key")!.Value));
    }

    [Fact]
    public async Task CreateRestoreOverlayAsync_PreservesDisabledAliasWhenEnabledAliasExists()
    {
        const string source = "https://example.com/aspire";
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            [new PackageMapping("Aspire*", source)],
            sources:
            [
                new NuGetConfigSource("disabledAlias", source, IsAmbient: true, IsEnabled: false),
                new NuGetConfigSource("private", source, IsAmbient: true, IsEnabled: true)
            ]);

        var document = XDocument.Load(config.ConfigFile.FullName);

        Assert.Empty(document.Descendants("disabledPackageSources"));
        Assert.Equal(
            ["disabledAlias", source, "private"],
            document.Descendants("packageSourceMapping")
                .Elements("packageSource")
                .Select(static mapping => mapping.Attribute("key")!.Value));
    }

    [Fact]
    public async Task CreateRestoreOverlayAsync_PreservesOtherDisabledAmbientSources()
    {
        const string selectedSource = "https://example.com/aspire";
        const string fallbackSource = "https://example.com/fallback";
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
        [
            new PackageMapping("Aspire*", selectedSource),
            new PackageMapping(PackageMapping.AllPackages, fallbackSource)
        ],
            sources:
            [
                new NuGetConfigSource("private", selectedSource, IsAmbient: true, IsEnabled: false),
                new NuGetConfigSource("selectedDisabledAlias", selectedSource, IsAmbient: true, IsEnabled: false),
                new NuGetConfigSource("fallbackDisabledAlias", fallbackSource, IsAmbient: true, IsEnabled: false),
                new NuGetConfigSource("fallback", fallbackSource, IsAmbient: true, IsEnabled: true)
            ],
            disabledAmbientSourceKeys:
            [
                "private",
                "selectedDisabledAlias",
                "fallbackDisabledAlias",
                "unrelated"
            ]);

        var document = XDocument.Load(config.ConfigFile.FullName);

        var disabledPackageSources = Assert.Single(document.Descendants("disabledPackageSources"));
        Assert.NotNull(disabledPackageSources.Element("clear"));
        Assert.Equal(
            ["selectedDisabledAlias", "fallbackDisabledAlias", "unrelated"],
            disabledPackageSources
                .Elements("add")
                .Select(static source => source.Attribute("key")!.Value));
        Assert.All(
            disabledPackageSources.Elements("add"),
            static source => Assert.Equal("true", source.Attribute("value")?.Value));
        Assert.Equal(
            ["private", selectedSource],
            document.Descendants("packageSourceMapping")
                .Elements("packageSource")
                .Where(source => source.Element("package")?.Attribute("pattern")?.Value == "Aspire*")
                .Select(static source => source.Attribute("key")!.Value));
    }

    [Fact]
    public async Task GenerateRestoreOverlayAsync_WritesStablePolicyBesideGeneratedProject()
    {
        var directory = Directory.CreateTempSubdirectory("aspire-restore-overlay-tests");
        try
        {
            var path = Path.Combine(directory.FullName, "NuGet.Config");

            await TemporaryNuGetConfig.GenerateRestoreOverlayAsync(
                [new PackageMapping("Aspire*", "https://example.com/aspire")],
                path,
                "/packages");

            var document = XDocument.Load(path);
            Assert.Equal(
                "/packages",
                document.Descendants("config")
                    .Elements("add")
                    .Single(element => element.Attribute("key")?.Value == "globalPackagesFolder")
                    .Attribute("value")?.Value);
            Assert.NotNull(document.Descendants("packageSourceMapping").Single().Element("clear"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CacheIdentity_DoesNotDependOnGlobalPackagesFolderLocation()
    {
        using var first = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            [new PackageMapping("Aspire*", "https://example.com/aspire")],
            configureGlobalPackagesFolder: true,
            globalPackagesFolderValue: "/packages/first");
        using var second = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            [new PackageMapping("Aspire*", "https://example.com/aspire")],
            configureGlobalPackagesFolder: true,
            globalPackagesFolderValue: "/packages/second");

        Assert.Equal(first.CacheIdentity, second.CacheIdentity);
    }
}
