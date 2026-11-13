// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using NuGet.Configuration;

namespace Aspire.Cli.Tests.NuGet;

public class TemporaryNuGetConfigFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CreatePreviewAsync_DisposeOwnsOnlySiblingDraft()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        const string original = "<configuration />";
        await File.WriteAllTextAsync(target.FullName, original);
        var content = Encoding.UTF8.GetBytes("<configuration><candidate /></configuration>");
        using var draft = await TemporaryNuGetConfigFile.CreatePreviewAsync(target, content, TestContext.Current.CancellationToken);

        Assert.Equal(target.DirectoryName, draft.ConfigFile.DirectoryName);
        Assert.Equal(content, await File.ReadAllBytesAsync(draft.ConfigFile.FullName));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(draft.ConfigFile.FullName));
        }
        draft.Dispose();

        Assert.Equal(original, await File.ReadAllTextAsync(target.FullName));
        Assert.True(Directory.Exists(target.DirectoryName));
        Assert.False(File.Exists(draft.ConfigFile.FullName));
    }

    [Fact]
    public async Task CreatePreviewAsync_InvalidContentCleansUpOnlyDraft()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        await File.WriteAllTextAsync(target.FullName, "<configuration />");

        await Assert.ThrowsAsync<System.Xml.XmlException>(() => TemporaryNuGetConfigFile.CreatePreviewAsync(
            target, Encoding.UTF8.GetBytes("<invalid"), TestContext.Current.CancellationToken));

        Assert.Equal([target.Name], workspace.WorkspaceRoot.EnumerateFiles().Select(static file => file.Name));
    }

    [Fact]
    public async Task CreateAsync_IncludesSourcesAndMappings()
    {
        using var config = await NuGetTestHelper.CreateStandaloneConfigurationAsync(
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
        using var config = await NuGetTestHelper.CreateStandaloneConfigurationAsync(
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
        using var config = await NuGetTestHelper.CreateStandaloneConfigurationAsync(
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

        var settings = Settings.LoadSpecificSettings(config.ConfigFile.DirectoryName!, config.ConfigFile.Name);
        var nativeMapping = PackageSourceMapping.GetPackageSourceMapping(settings);

        Assert.Equal(
            sources,
            new PackageSourceProvider(settings).LoadPackageSources().Select(source => source.Source));
        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources("Upper.Example"));
        Assert.Equal(["aspire-standalone-0"], nativeMapping.GetConfiguredPackageSources("Lower.Example"));
    }

    [Fact]
    public async Task CreateRestoreOverlayAsync_UsesProvidedWriter()
    {
        using var config = await TemporaryNuGetConfigFile.CreateAsync(
            path => File.WriteAllText(
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
        using var config = await TemporaryNuGetConfigFile.CreateAsync(
            path => File.WriteAllText(
                path,
                "<configuration><packageSourceMapping><clear /></packageSourceMapping></configuration>"));
        var originalIdentity = config.CacheIdentity;

        await config.RegenerateAsync(
            path => File.WriteAllText(
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
        using var config = await TemporaryNuGetConfigFile.CreateAsync(
            path => File.WriteAllText(path, "<configuration />"));
        var directory = config.ConfigFile.Directory!.FullName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => config.RegenerateAsync(path =>
        {
            File.Delete(path);
            throw new InvalidOperationException("Failed to regenerate the configuration.");
        }));

        Assert.False(config.ConfigFile.Exists);
        Assert.True(Directory.Exists(directory));

        config.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CacheIdentity_DoesNotDependOnGlobalPackagesFolderLocation()
    {
        using var first = await TemporaryNuGetConfigFile.CreateAsync(
            path => File.WriteAllText(
                path,
                "<configuration><config><add key=\"globalPackagesFolder\" value=\"/packages/first\" /></config></configuration>"));
        using var second = await TemporaryNuGetConfigFile.CreateAsync(
            path => File.WriteAllText(
                path,
                "<configuration><config><add key=\"globalPackagesFolder\" value=\"/packages/second\" /></config></configuration>"));

        Assert.Equal(first.CacheIdentity, second.CacheIdentity);
    }

    [Fact]
    public async Task CacheIdentity_DoesNotChangeWhenGlobalPackagesFolderIsAdded()
    {
        using var config = await TemporaryNuGetConfigFile.CreateAsync(
            path => File.WriteAllText(path, "<configuration />"));
        var originalIdentity = config.CacheIdentity;

        await config.RegenerateAsync(
            path => File.WriteAllText(
                path,
                "<configuration><config><add key=\"globalPackagesFolder\" value=\"/packages\" /></config></configuration>"));

        Assert.Equal(originalIdentity, config.CacheIdentity);
    }
}
