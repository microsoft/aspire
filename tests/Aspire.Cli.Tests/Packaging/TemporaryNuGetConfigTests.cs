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
    public async Task CreateRestoreOverlayAsync_UsesProvidedWriter()
    {
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => File.WriteAllTextAsync(
                path,
                """
                <configuration>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="Aspire*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """));

        var document = XDocument.Load(config.ConfigFile.FullName);

        var mapping = Assert.Single(document.Descendants("packageSourceMapping"));
        Assert.NotNull(mapping.Element("clear"));
        Assert.Equal("private", mapping.Element("packageSource")?.Attribute("key")?.Value);
        Assert.Equal("Aspire*", mapping.Descendants("package").Single().Attribute("pattern")?.Value);
    }

    [Fact]
    public async Task RegenerateAsync_RewritesConfigAndCacheIdentity()
    {
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => File.WriteAllTextAsync(
                path,
                "<configuration><packageSourceMapping><clear /></packageSourceMapping></configuration>"));
        var originalIdentity = config.CacheIdentity;

        await config.RegenerateAsync(
            path => File.WriteAllTextAsync(
                path,
                "<configuration><packageSourceMapping><clear /><packageSource key=\"private\"><package pattern=\"Aspire*\" /></packageSource></packageSourceMapping></configuration>"));

        Assert.NotEqual(originalIdentity, config.CacheIdentity);
        Assert.Equal(
            "Aspire*",
            XDocument.Load(config.ConfigFile.FullName)
                .Descendants("package")
                .Single()
                .Attribute("pattern")?
                .Value);
    }

    [Fact]
    public async Task Dispose_RemovesDirectoryWhenFailedRegenerationDeletedConfig()
    {
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => File.WriteAllTextAsync(path, "<configuration />"));
        var directory = config.ConfigFile.Directory!.FullName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => config.RegenerateAsync(path =>
        {
            File.Delete(path);
            return Task.FromException(new InvalidOperationException("Failed to regenerate the configuration."));
        }));

        Assert.False(config.ConfigFile.Exists);
        Assert.True(Directory.Exists(directory));

        config.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CacheIdentity_DoesNotDependOnGlobalPackagesFolderLocation()
    {
        using var first = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => File.WriteAllTextAsync(
                path,
                "<configuration><config><add key=\"globalPackagesFolder\" value=\"/packages/first\" /></config></configuration>"));
        using var second = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => File.WriteAllTextAsync(
                path,
                "<configuration><config><add key=\"globalPackagesFolder\" value=\"/packages/second\" /></config></configuration>"));

        Assert.Equal(first.CacheIdentity, second.CacheIdentity);
    }

    [Fact]
    public async Task CacheIdentity_DoesNotChangeWhenGlobalPackagesFolderIsAdded()
    {
        using var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => File.WriteAllTextAsync(path, "<configuration />"));
        var originalIdentity = config.CacheIdentity;

        await config.RegenerateAsync(
            path => File.WriteAllTextAsync(
                path,
                "<configuration><config><add key=\"globalPackagesFolder\" value=\"/packages\" /></config></configuration>"));

        Assert.Equal(originalIdentity, config.CacheIdentity);
    }
}
