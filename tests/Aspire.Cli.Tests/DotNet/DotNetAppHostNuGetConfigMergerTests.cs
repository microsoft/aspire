// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Xml.Linq;
using System.Xml;
using Aspire.Cli.Packaging;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

public class DotNetAppHostNuGetConfigMergerTests
{
    private readonly ITestOutputHelper _outputHelper;

    public DotNetAppHostNuGetConfigMergerTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    private static async Task<FileInfo> WriteConfigAsync(DirectoryInfo dir, string content)
    {
        var path = Path.Combine(dir.FullName, "nuget.config");
        await File.WriteAllTextAsync(path, content);
        return new FileInfo(path);
    }

    private static PackageChannel CreateChannel(PackageMapping[] mappings) => PackageChannel.CreateExplicitChannel("test", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareAsync_UnmappedAmbientSourceDoesNotRequireAliasOwnership(bool enabled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string source = "https://company.example/v3/index.json";
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear /><add key="company" value="{{source}}" /></packageSources>
              <disabledPackageSources>{{(enabled ? "" : """<add key="company" value="true" />""")}}</disabledPackageSources>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        var channel = CreateChannel([new("Aspire*", source)]);

        if (enabled)
        {
            var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
                directory, channel, createIfMissing: true, TestContext.Current.CancellationToken);
            Assert.NotNull(candidate);
            await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
            Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Other.Dependency"));
            Assert.Empty(XDocument.Load(candidate.TargetFile.FullName).Descendants("packageSource"));
        }
        else
        {
            await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
                    directory, channel, createIfMissing: true, TestContext.Current.CancellationToken));
            Assert.Empty(directory.EnumerateFiles());
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Theory]
    [InlineData(false, "company", "https://feed.example/v3/index.json")]
    [InlineData(true, "company", "https://feed.example/v3/index.json")]
    [InlineData(true, "aspire-staging", "https://feed.example/v3/index.json")]
    [InlineData(false, "company", "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-existing/nuget/v3/index.json")]
    public async Task PrepareAsync_RefusesBorrowingExistingChannelFeed(
        bool inherited, string sourceKey, string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var target = workspace.CreateDirectory("apphost");
        var existingDirectory = inherited ? workspace.WorkspaceRoot : target;
        var existingFile = await WriteConfigAsync(existingDirectory, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="{{sourceKey}}" value="{{source}}" />
              </packageSources>
              <packageSourceCredentials>
                <{{sourceKey}}>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="test-password" />
                </{{sourceKey}}>
              </packageSourceCredentials>
              <packageSourceMapping>
                <clear />
                <packageSource key="{{sourceKey}}"><package pattern="Private.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(existingFile.FullName);

        var error = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
                target, CreateChannel([new("Aspire*", source)]), createIfMissing: true,
                TestContext.Current.CancellationToken));

        Assert.Equal(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat,
            Path.Combine(target.FullName, "nuget.config"), sourceKey), error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(existingFile.FullName));
        if (inherited)
        {
            Assert.Empty(target.EnumerateFiles());
        }
    }

    [Fact]
    public async Task PrepareAsync_MaintainsExistingLocalCliOwnedChannelFeed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string source = "https://feed.example/v3/index.json";
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear /><add key="aspire-test" value="{{source}}" /></packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="aspire-test"><package pattern="Private.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            workspace.WorkspaceRoot, CreateChannel([new("Aspire*", source)]), createIfMissing: true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Private.Library"));
        Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Aspire.Hosting"));
        Assert.Single(XDocument.Load(target.FullName).Root!.Element("packageSources")!.Elements("add"));
        Assert.Single(XDocument.Load(target.FullName).Root!.Element("packageSourceMapping")!.Elements("clear"));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task PrepareAsync_RefusesEditsRequiringDisabledChannelSource(
        bool inheritedDisabledSetting, bool requiresEdit, bool clearsDisabledLocally)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string disabledSetting = """<add key="aspire-test" value="true" />""";
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear /><add key="company" value="https://company.example/v3/index.json" /></packageSources>
              <disabledPackageSources>{{(inheritedDisabledSetting ? disabledSetting : "")}}</disabledPackageSources>
              <packageSourceMapping><packageSource key="company"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        var originalParent = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        const string source = "https://selected.example/v3/index.json";
        var target = await WriteConfigAsync(directory, $$"""
            <configuration>
              <packageSources><add key="aspire-test" value="{{source}}" /></packageSources>
              <disabledPackageSources>
                {{(clearsDisabledLocally ? "<clear />" : "")}}
                {{(!inheritedDisabledSetting ? disabledSetting : "")}}
              </disabledPackageSources>
              <packageSourceMapping>
                <packageSource key="aspire-test"><package pattern="{{(requiresEdit ? "Private.*" : "Aspire*")}}" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(target.FullName);
        var originalDisabled = XDocument.Load(target.FullName).Root!.Element("disabledPackageSources")!.ToString();
        var service = NuGetTestHelper.CreateService();
        var configuration = service.BuildAppHostConfiguration(
            directory, "test", [new("Aspire*", source)], TestContext.Current.CancellationToken);
        Assert.Equal(clearsDisabledLocally, Assert.Single(configuration.ConfigSources).IsEnabled);
        var merger = new DotNetAppHostNuGetConfigMerger(service);

        if (requiresEdit && !clearsDisabledLocally)
        {
            var error = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                merger.PrepareAsync(directory, configuration, createIfMissing: true, globalPackagesFolder: null,
                    TestContext.Current.CancellationToken));
            Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat, target.FullName, "aspire-test"), error.Message);
        }
        else
        {
            var candidate = await merger.PrepareAsync(
                directory, configuration, createIfMissing: true, globalPackagesFolder: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(requiresEdit, candidate is not null);
            if (candidate is not null)
            {
                Assert.Equal(
                    originalDisabled,
                    XDocument.Parse(candidate.GetProposedDocument().OuterXml).Root!.Element("disabledPackageSources")!.ToString());
            }
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
        Assert.Equal(originalParent, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PrepareAsync_LocalChannelSourceDoesNotTakeOwnershipOfInheritedMappings(
        bool hasLocalMapping, bool clearsMappings)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string source = "https://feed.example/v3/index.json";
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources><clear /></packageSources>
              <packageSourceMapping>
                <packageSource key="aspire-test"><package pattern="Private.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var originalParent = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        var localMapping = hasLocalMapping
            ? """<packageSource key="aspire-test"><package pattern="Local.*" /></packageSource>"""
            : string.Empty;
        var target = await WriteConfigAsync(directory, $$"""
            <configuration>
              <packageSources><add key="aspire-test" value="{{source}}" /></packageSources>
              <packageSourceMapping>
                {{(clearsMappings ? "<clear />" : "")}}
                {{localMapping}}
              </packageSourceMapping>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(target.FullName);
        var channel = CreateChannel([new("Aspire*", source)]);

        if (!hasLocalMapping && !clearsMappings)
        {
            Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Private.Library"));
            await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
                    directory, channel, createIfMissing: true, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
            Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Private.Library"));
        }
        else
        {
            var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
                directory, channel, createIfMissing: true, TestContext.Current.CancellationToken);
            Assert.Equal(hasLocalMapping, candidate is not null);
            if (candidate is not null)
            {
                await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
            }
            Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Aspire.Hosting"));
            Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Local.Library"));
        }

        Assert.Equal(originalParent, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CreatesConfigFromMappings_WhenNoExistingConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.CreateDirectory("apphost");
        await WriteConfigAsync(workspace.WorkspaceRoot, "<configuration><packageSources><clear /></packageSources></configuration>");

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("CommunityToolkit*", "https://feed2.example")
        };

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, mappings).DefaultTimeout();

        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));

        await Verify(XDocument.Load(targetConfigPath).ToString(), "xml");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_GeneratesConfigFromMappings_WhenChannelProvided()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.CreateDirectory("apphost");
        await WriteConfigAsync(workspace.WorkspaceRoot, "<configuration><packageSources><clear /></packageSources></configuration>");

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("CommunityToolkit*", "https://feed2.example")
        };

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, mappings).DefaultTimeout();

        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));

        var xml = XDocument.Load(targetConfigPath);
        var packageSources = xml.Root!.Element("packageSources")!;
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://feed1.example");
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://feed2.example");

        var psm = xml.Root!.Element("packageSourceMapping");
        Assert.Null(psm);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_AddsMissingSources_WhenUpdatingExistingConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config with one source only
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="aspire-test" value="https://feed1.example" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="aspire-test">
                        <package pattern="Aspire.*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("Microsoft.*", "https://feed2.example") // feed2 missing
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://feed2.example");

        // Ensure existing mapping retained
        var psm = xml.Root!.Element("packageSourceMapping")!;
        Assert.NotNull(psm.Elements("packageSource").First().Elements("package").FirstOrDefault(p => (string?)p.Attribute("pattern") == "Aspire.*"));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PreservesUserMappingsForSelectedPattern()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config: pattern Lib.* mapped to old source only
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="https://old.example" value="https://old.example" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://old.example">
                        <package pattern="Lib.*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Lib.*", "https://new.example")
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        Assert.Equal(["https://old.example", "https://new.example"],
            packageSources.Elements("add").Select(e => (string?)e.Attribute("value")));
        Assert.Equal(["https://new.example", "https://old.example"],
            NuGetTestHelper.GetEligiblePackageSources(root.FullName, "Lib.Example"));
        await Verify(xml.ToString(), "xml");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RetiresStagingPolicyWithoutAddingStableMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;
        const string stagingSource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/aspire-staging/nuget/v3/index.json";
        const string stableSource = "https://api.nuget.org/v3/index.json";

        await WriteConfigAsync(root,
            $$"""
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="aspire-staging" value="{{stagingSource}}" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="aspire-staging">
                        <package pattern="Aspire.*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("*", stableSource)
        };

        var channel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var sources = NuGetTestHelper.CreateClient().GetSettings(root.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]).Sources;
        Assert.Equal(["nuget.org"], sources.Where(static source => source.IsEnabled).Select(static source => source.Name));

        Assert.Null(xml.Root!.Element("packageSourceMapping"));
        Assert.Equal([stableSource], NuGetTestHelper.GetEligiblePackageSources(root.FullName, "Aspire.Hosting"));
        Assert.Equal([stableSource], NuGetTestHelper.GetEligiblePackageSources(root.FullName, "Example.Dependency"));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PreservesUnmappedSources_WhenMappingAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config without packageSourceMapping
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="aspire-test" value="https://feed1.example" />
                    <add key="aspire-test-0" value="https://feed2.example" />
                </packageSources>
            </configuration>
            """);

        var target = Path.Combine(root.FullName, "nuget.config");
        var original = await File.ReadAllBytesAsync(target);
        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("Microsoft.*", "https://feed2.example")
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        var xml = XDocument.Load(target);
        var psm = xml.Root!.Element("packageSourceMapping");
        Assert.Null(psm);
        Assert.Equal(["https://feed1.example", "https://feed2.example"],
            NuGetTestHelper.GetEligiblePackageSources(root.FullName, "Other.Dependency"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CreateOrUpdateAsync_PreservesEffectiveMappingMode(
        bool parentHasMapping, bool hasLocalMappingSection, bool clearsMappings)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string companySource = "https://company.example/v3/index.json";
        const string channelSource = "https://selected.example/v3/index.json";
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear /><add key="company" value="{{companySource}}" /></packageSources>
              {{(parentHasMapping ? """<packageSourceMapping><packageSource key="company"><package pattern="*" /></packageSource></packageSourceMapping>""" : "")}}
            </configuration>
            """);
        var parentBytes = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        var mappingSection = hasLocalMappingSection
            ? $"<packageSourceMapping>{(clearsMappings ? "<clear />" : "")}</packageSourceMapping>"
            : "";
        var target = await WriteConfigAsync(directory, $"<configuration>{mappingSection}</configuration>");

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(
            directory, CreateChannel([new("Aspire*", channelSource)])).DefaultTimeout();

        var mappingEnabled = parentHasMapping && !clearsMappings;
        var document = XDocument.Load(target.FullName);
        Assert.Equal([channelSource], document.Root!.Element("packageSources")!.Elements("add")
            .Select(static source => (string?)source.Attribute("value")));
        Assert.Equal(mappingEnabled ? 1 : 0,
            document.Root.Element("packageSourceMapping")?.Elements("packageSource").Count() ?? 0);
        Assert.Equal(clearsMappings ? 1 : 0,
            document.Root.Element("packageSourceMapping")?.Elements("clear").Count() ?? 0);
        Assert.Equal(mappingEnabled ? [channelSource] : [companySource, channelSource],
            NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Aspire.Hosting.Redis"));
        Assert.Equal(mappingEnabled ? [companySource] : [companySource, channelSource],
            NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Other.Dependency"));
        Assert.Equal(parentBytes, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Fact]
    public void HasMissingSources_ReturnsTrue_WhenConfigAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;
        var mappings = new[] { new PackageMapping("Aspire.*", "https://feed.example") };
        var channel = CreateChannel(mappings);
        Assert.True(DotNetAppHostNuGetConfigTestHelper.HasMissingSources(root, channel));
    }

    [Fact]
    public async Task HasMissingSources_ReturnsTrue_WhenPatternMappedToWrongSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="https://feed1.example" value="https://feed1.example" />
                    <add key="https://feed2.example" value="https://feed2.example" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://feed1.example">
                        <package pattern="Aspire.*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed2.example") // should be feed2, but config has feed1
        };

        var channel = CreateChannel(mappings);
        Assert.True(DotNetAppHostNuGetConfigTestHelper.HasMissingSources(root, channel));
    }

    [Fact]
    public async Task HasMissingSources_ReturnsFalse_WhenAllSourcesAndMappingsPresent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="https://feed1.example" value="https://feed1.example" />
                    <add key="https://feed2.example" value="https://feed2.example" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://feed1.example">
                        <package pattern="Aspire.*" />
                    </packageSource>
                    <packageSource key="https://feed2.example">
                        <package pattern="Microsoft.*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("Microsoft.*", "https://feed2.example")
        };

        var channel = CreateChannel(mappings);
        Assert.False(DotNetAppHostNuGetConfigTestHelper.HasMissingSources(root, channel));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_DoesNotPersistDefaultSourceMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="feed1" value="https://feed1.example" />
                    <add key="feed2" value="https://feed2.example" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="feed1">
                        <package pattern="*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("*", "https://feed1.example"),
            new PackageMapping("*", "https://feed2.example")
        };
        var configPath = Path.Combine(root.FullName, "nuget.config");
        var original = await File.ReadAllBytesAsync(configPath);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, mappings).DefaultTimeout();

        var document = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var mappedSources = document.Descendants("packageSourceMapping")
            .Elements("packageSource")
            .Where(element => element.Elements("package").Any(package => package.Attribute("pattern")?.Value == "*"))
            .Select(element => element.Attribute("key")!.Value)
            .ToArray();

        Assert.Equal(["feed1"], mappedSources);
        Assert.Equal(original, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_IntegrationConfigurationDoesNotCaptureUserDefaultAliases()
    {
        using var generator = TemporaryWorkspace.CreateForCli(_outputHelper);
        using var consumer = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string generatorConfig = """
            <configuration>
              <packageSources><clear /><add key="company-public" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <packageSourceMapping><packageSource key="company-public"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """;
        const string consumerConfig = """
            <configuration>
              <packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <packageSourceMapping><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """;
        var generatorParent = await WriteConfigAsync(generator.WorkspaceRoot, generatorConfig);
        var consumerParent = await WriteConfigAsync(consumer.WorkspaceRoot, consumerConfig);
        var output = generator.CreateDirectory("apphost");
        var destination = consumer.CreateDirectory("apphost");
        const string channelSource = "https://channel.example/v3/index.json";
        var channel = CreateChannel([new("Aspire*", channelSource), new("*", PackageSources.NuGetOrg)]);

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(output, channel);
        var generated = await File.ReadAllBytesAsync(Path.Combine(output.FullName, "nuget.config"));
        await File.WriteAllBytesAsync(Path.Combine(destination.FullName, "nuget.config"), generated);

        Assert.Equal([channelSource], NuGetTestHelper.GetEligiblePackageSources(destination.FullName, "Aspire.Hosting"));
        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(destination.FullName, "Microsoft.Extensions.Options"));
        Assert.Equal(generatorConfig, await File.ReadAllTextAsync(generatorParent.FullName));
        Assert.Equal(consumerConfig, await File.ReadAllTextAsync(consumerParent.FullName));
        await Verify(XDocument.Load(Path.Combine(output.FullName, "nuget.config")).ToString(), "xml");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RefusesMultipleUserOwnedAliasesForChannelFeed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;
        const string sourceUrl = "https://private.example/v3/index.json";

        await WriteConfigAsync(root,
            $$"""
            <configuration>
              <packageSources>
                <add key="authenticated" value="{{sourceUrl}}" />
                <add key="anonymousAlias" value="{{sourceUrl}}" />
              </packageSources>
              <packageSourceCredentials>
                <authenticated>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </authenticated>
              </packageSourceCredentials>
              <packageSourceMapping>
                <packageSource key="authenticated"><package pattern="Private.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var mappings = new[]
        {
            new PackageMapping("Aspire*", sourceUrl)
        };
        var channel = CreateChannel(mappings);
        var configPath = Path.Combine(root.FullName, "nuget.config");
        var original = await File.ReadAllBytesAsync(configPath);

        await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel));

        Assert.Equal(original, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PreservesAmbientSourceKeysWithoutPersistingDefaultMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config with custom key names (like "nuget" instead of URL)
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
                    <add key="dotnet9" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                </packageSources>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://example.com/aspire-feed"),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json")
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;

        // Existing sources should still be present with their original keys
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "nuget" && (string?)e.Attribute("value") == "https://api.nuget.org/v3/index.json");
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "dotnet9" && (string?)e.Attribute("value") == "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json");

        // New source should be added
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://example.com/aspire-feed");

        Assert.Null(xml.Root.Element("packageSourceMapping"));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_DoesNotSynthesizeMappingsForExistingSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Scenario from @mitchdenny: config has multiple sources but NO packageSourceMapping
        // This means all sources can serve all packages (implicit behavior)
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="custom" value="https://example.com/custom/nuget/v3/index.json" />
                </packageSources>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://example.com/aspire-daily")
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;

        // All original sources should still be present
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "nuget.org");
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "custom");

        // New aspire source should be added
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://example.com/aspire-daily");

        Assert.Null(xml.Root.Element("packageSourceMapping"));
        Assert.Equal(
            ["https://api.nuget.org/v3/index.json", "https://example.com/aspire-daily", "https://example.com/custom/nuget/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(root.FullName, "Other.Dependency"));
        await Verify(xml.ToString(), "xml");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_AddsSpecificMappings_WhenExistingWildcardMappingPresent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Scenario: existing config already has a wildcard mapping on nuget.org
        // When we add explicit mappings for Aspire packages to a new source,
        // the code should add the new mappings without interfering with the existing wildcard
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="nuget.org">
                        <package pattern="*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        // aspire update adds specific mappings for Aspire packages to a new channel
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://example.com/aspire-daily"),
            new PackageMapping("Microsoft.Extensions.ServiceDiscovery*", "https://example.com/aspire-daily")
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;

        // Original source should still be present
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "nuget.org");

        // New aspire source should be added
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://example.com/aspire-daily");

        // Debug: Print the XML to understand what's happening
        _outputHelper.WriteLine("Generated XML:");
        _outputHelper.WriteLine(xml.ToString());

        // Package source mapping should have both the original wildcard and the new specific mappings
        var psm = xml.Root!.Element("packageSourceMapping")!;

        // Original nuget.org should still have the wildcard pattern
        var nugetMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "nuget.org");
        Assert.NotNull(nugetMapping);
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");

        // The aspire source should have its specific patterns
        var aspireMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "aspire-test");
        Assert.NotNull(aspireMapping);
        Assert.Contains(aspireMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Aspire*");
        Assert.Contains(aspireMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Microsoft.Extensions.ServiceDiscovery*");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_DoesNotAddWildcardToPrivateSourceWithExistingPatterns()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // User has nuget.org with wildcard and a private source with specific patterns
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="github" value="https://nuget.pkg.github.com/myorg/index.json" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="nuget.org">
                        <package pattern="*" />
                    </packageSource>
                    <packageSource key="github">
                        <package pattern="myorg.*" />
                        <package pattern="other" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        // Stable channel: maps everything to nuget.org
        var mappings = new[]
        {
            new PackageMapping("*", "https://api.nuget.org/v3/index.json")
        };

        var channel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable, PackageChannelQuality.Stable, mappings,
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var psm = xml.Root!.Element("packageSourceMapping")!;

        // nuget.org should retain the wildcard
        var nugetMapping = psm.Elements("packageSource")
            .FirstOrDefault(ps => (string?)ps.Attribute("key") == "nuget.org");
        Assert.NotNull(nugetMapping);
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");

        // The private source should keep its original patterns without a wildcard being added
        var githubMapping = psm.Elements("packageSource")
            .FirstOrDefault(ps => (string?)ps.Attribute("key") == "github");
        Assert.NotNull(githubMapping);
        Assert.Contains(githubMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "myorg.*");
        Assert.Contains(githubMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "other");
        Assert.DoesNotContain(githubMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RemovesUnrequiredSources_InsteadOfAddingWildcardPattern()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config with a PR hive source that should be removed and a user-defined source that should be preserved
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="https://valid.example" value="https://valid.example" />
                    <add key="C:\Users\user\.aspire\hives\invalid-pr" value="C:\Users\user\.aspire\hives\invalid-pr" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://valid.example">
                        <package pattern="ValidPkg*" />
                    </packageSource>
                    <packageSource key="C:\Users\user\.aspire\hives\invalid-pr">
                        <package pattern="Aspire*" />
                        <package pattern="Microsoft.Extensions.ServiceDiscovery*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        // Remap the CLI-owned hive's patterns to a new channel feed.
        const string selectedSource = "https://new.example/v3/index.json";
        var mappings = new[]
        {
            new PackageMapping("Aspire*", selectedSource),
            new PackageMapping("Microsoft.Extensions.ServiceDiscovery*", selectedSource),
            new PackageMapping("*", selectedSource)
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;

        // The PR hive source should be removed because it's safe to remove and no longer needed
        Assert.DoesNotContain(packageSources.Elements("add"),
            e => (string?)e.Attribute("value") == "C:\\Users\\user\\.aspire\\hives\\invalid-pr");

        // The user-defined source should be preserved even though its patterns were remapped
        Assert.Contains(packageSources.Elements("add"),
            e => (string?)e.Attribute("value") == "https://valid.example");

        var sources = NuGetTestHelper.CreateClient().GetSettings(root.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]).Sources;
        Assert.Contains(sources, static source => source.Name == "aspire-test" && source.IsEnabled);

        var psm = xml.Root!.Element("packageSourceMapping")!;

        // The PR hive source should not have any mapping entries (removed entirely)
        Assert.DoesNotContain(psm.Elements("packageSource"),
            ps => (string?)ps.Attribute("key") == "C:\\Users\\user\\.aspire\\hives\\invalid-pr");

        // The user-defined source should keep its original patterns without a wildcard being added
        var validExampleMapping = psm.Elements("packageSource")
            .FirstOrDefault(ps => (string?)ps.Attribute("key") == "https://valid.example");
        Assert.NotNull(validExampleMapping);
        Assert.Contains(validExampleMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "ValidPkg*");
        Assert.DoesNotContain(validExampleMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");

        // The new channel feed gets only the integration-specific patterns.
        var nugetMapping = psm.Elements("packageSource")
            .FirstOrDefault(ps => (string?)ps.Attribute("key") == "aspire-test");
        Assert.NotNull(nugetMapping);
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Aspire*");
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Microsoft.Extensions.ServiceDiscovery*");
        Assert.Equal(
            ["Aspire*", "Microsoft.Extensions.ServiceDiscovery*"],
            nugetMapping.Elements("package").Select(p => (string)p.Attribute("pattern")!));

        // There should be two packageSource elements (the channel feed and valid.example).
        Assert.Equal(2, psm.Elements("packageSource").Count());
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RemovesOldPrHive_WhenSwitchingBetweenPrHives()
    {
        // Reproduces the scenario reported on `aspire update --channel pr-<new>` when the
        // previous channel was also a PR hive: switching between two `~/.aspire/hives/pr-*/packages`
        // sources must remove the old source from <packageSources>, not just from
        // <packageSourceMapping>. If the stale path lingers, subsequent `dotnet restore`
        // fails with NU1301 when that hive directory has since been deleted/cleaned.
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var oldHive = workspace.CreateDirectory(".aspire/hives/pr-17182/packages").FullName;
        var newHive = workspace.CreateDirectory(".aspire/hives/pr-17192/packages").FullName;

        await WriteConfigAsync(root,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
                <packageSources>
                    <add key="{oldHive}" value="{oldHive}" />
                    <add key="https://api.nuget.org/v3/index.json" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="{oldHive}">
                        <package pattern="Aspire*" />
                    </packageSource>
                    <packageSource key="https://api.nuget.org/v3/index.json">
                        <package pattern="*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire*", newHive),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json"),
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;

        Assert.DoesNotContain(packageSources.Elements("add"),
            e => string.Equals((string?)e.Attribute("value"), oldHive, StringComparison.Ordinal));
        Assert.Contains(packageSources.Elements("add"),
            e => string.Equals((string?)e.Attribute("value"), newHive, StringComparison.Ordinal));

        var psm = xml.Root!.Element("packageSourceMapping")!;
        Assert.DoesNotContain(psm.Elements("packageSource"),
            ps => string.Equals((string?)ps.Attribute("key"), oldHive, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RemovesOldPrHive_WhenItHasNoMappingElement()
    {
        // This is the *real* shape of the regression reported on pr-17192 follow-up:
        // the AppHost-level nuget.config had pr-17182 listed in <packageSources> but the
        // <packageSourceMapping> contained no entry for that source at all. Because
        // RemoveEmptyPackageSourceElements only cleans up entries whose mapping element
        // became empty *during the merge*, a source that never had a mapping element
        // survives the merge and breaks subsequent `dotnet restore` once the hive
        // directory is deleted.
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        const string oldHive = "/Users/midenn/.aspire/hives/pr-17182/packages";
        const string newHive = "/Users/midenn/.aspire/hives/pr-17192/packages";

        await WriteConfigAsync(root,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
                <packageSources>
                    <add key="{oldHive}" value="{oldHive}" />
                </packageSources>
                <packageSourceMapping>
                </packageSourceMapping>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire*", newHive),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json"),
        };

        var channel = CreateChannel(mappings);
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;

        Assert.DoesNotContain(packageSources.Elements("add"),
            e => string.Equals((string?)e.Attribute("value"), oldHive, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CallbackInvokedForNewConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);

        bool callbackInvoked = false;
        FileInfo? callbackTargetFile = null;
        XmlDocument? callbackOriginalContent = null;
        XmlDocument? callbackProposedContent = null;

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
        {
            callbackInvoked = true;
            callbackTargetFile = targetFile;
            callbackOriginalContent = originalContent;
            callbackProposedContent = proposedContent;
            return Task.FromResult(true); // Proceed with the update
        });

        // Verify callback was invoked
        Assert.True(callbackInvoked);
        Assert.NotNull(callbackTargetFile);
        Assert.Null(callbackOriginalContent); // Should be null for new files
        Assert.NotNull(callbackProposedContent);

        // Verify file was created
        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));
        Assert.Equal(targetConfigPath, callbackTargetFile.FullName);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CallbackCanPreventNewConfigCreation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);

        bool callbackInvoked = false;

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
        {
            callbackInvoked = true;
            return Task.FromResult(false); // Prevent the update
        });

        // Verify callback was invoked
        Assert.True(callbackInvoked);

        // Verify file was NOT created
        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.False(File.Exists(targetConfigPath));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CallbackInvokedForExistingConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Create an existing config
        var existingConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """;

        await WriteConfigAsync(root, existingConfig).DefaultTimeout();

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);

        bool callbackInvoked = false;
        FileInfo? callbackTargetFile = null;
        XmlDocument? callbackOriginalContent = null;
        XmlDocument? callbackProposedContent = null;

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
        {
            callbackInvoked = true;
            callbackTargetFile = targetFile;
            callbackOriginalContent = originalContent;
            callbackProposedContent = proposedContent;
            return Task.FromResult(true); // Proceed with the update
        });

        // Verify callback was invoked
        Assert.True(callbackInvoked);
        Assert.NotNull(callbackTargetFile);
        Assert.NotNull(callbackOriginalContent); // Should have original content for existing files
        Assert.NotNull(callbackProposedContent);

        // Verify file exists and was updated
        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));
        Assert.Equal(targetConfigPath, callbackTargetFile.FullName);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CallbackCanPreventExistingConfigUpdate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Create an existing config
        var existingConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """;

        await WriteConfigAsync(root, existingConfig).DefaultTimeout();
        var originalContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "nuget.config")).DefaultTimeout();

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);

        bool callbackInvoked = false;

        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
        {
            callbackInvoked = true;
            return Task.FromResult(false); // Prevent the update
        });

        // Verify callback was invoked
        Assert.True(callbackInvoked);

        // Verify file content was NOT changed
        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        var currentContent = await File.ReadAllTextAsync(targetConfigPath);
        Assert.Equal(NormalizeLineEndings(originalContent), NormalizeLineEndings(currentContent));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_WorksWithoutCallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);

        // Call without callback - should work as before
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(root, channel).DefaultTimeout();

        // Verify file was created
        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));
    }

    [Fact]
    public async Task PrepareAsync_DefersExistingConfigChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var original = """
            <configuration>
              <packageSources>
                <add key="private" value="https://private.example/v3/index.json" protocolVersion="3" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="test-password" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """;
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, original);
        var channel = CreateChannel([new PackageMapping("Aspire*", "https://feed.example/v3/index.json")]);

        var update = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            workspace.WorkspaceRoot, channel, createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal(target.FullName, update.TargetFile.FullName);
        Assert.Equal(original, await File.ReadAllTextAsync(target.FullName));
        Assert.Equal(XDocument.Parse(original).ToString(), XDocument.Parse(update.GetOriginalDocument()!.OuterXml).ToString());
        await Verify(XDocument.Parse(update.GetProposedDocument().OuterXml).ToString(), "xml");
    }

    [Fact]
    public async Task PrepareAsync_DoesNotCreateTargetDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var directory = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "new-config"));
        var channel = CreateChannel([new PackageMapping("Aspire*", "https://feed.example/v3/index.json")]);

        var update = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            directory, channel, createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Null(update.OriginalContent);
        Assert.False(Directory.Exists(directory.FullName));
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(update, TestContext.Current.CancellationToken);
        Assert.Equal(XDocument.Parse(update.GetProposedDocument().OuterXml).ToString(), XDocument.Load(update.TargetFile.FullName).ToString());
    }

    [Fact]
    public async Task PrepareAsync_NoCreatePolicyLeavesMissingConfigAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var directory = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "new-config"));
        var channel = CreateChannel([new PackageMapping("*", "https://feed.example/v3/index.json")]);

        var update = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            directory, channel, createIfMissing: false, TestContext.Current.CancellationToken);

        Assert.Null(update);
        Assert.False(Directory.Exists(directory.FullName));
    }

    [Fact]
    public async Task PrepareAsync_EquivalentPolicyPreservesExistingBytes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <!-- Preserve formatting and unrelated mappings on a no-op update. -->
              <packageSources>
                <clear />
                <add key="company" value="https://feed.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="company">
                  <package pattern="Company.*" />
                  <package pattern="ASPIRE*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(target.FullName);

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            workspace.WorkspaceRoot, CreateChannel([new("Aspire*", "https://feed.example/v3/index.json")]),
            createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.Null(candidate);
        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
    }

    [Fact]
    public async Task PrepareAsync_EquivalentInheritedPolicyDoesNotCreateChildConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources>
                <clear />
                <add key="company" value="https://feed.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="company">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            appHostDirectory, CreateChannel([new("Aspire*", "https://feed.example/v3/index.json")]),
            createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.Null(candidate);
        Assert.Empty(appHostDirectory.EnumerateFiles());
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    public async Task PrepareAsync_ChannelCleanupOnlyChangesLocalEntries(
        bool locallyDefined, bool clearSources, bool clearMappings, bool hasUnrelatedPattern)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string oldSource = "https://old.example/v3/index.json";
        const string newSource = "https://new.example/v3/index.json";
        var oldMapping = $$"""
            <packageSource key="aspire-staging">
              <package pattern="Aspire.Hosting.Redis" />
              {{(hasUnrelatedPattern ? """<package pattern="Private.*" />""" : "")}}
            </packageSource>
            """;
        const string oldSourceEntry = """<add key="aspire-staging" value="https://old.example/v3/index.json" />""";
        const string companySource = """<add key="company" value="https://company.example/v3/index.json" />""";
        const string companyMapping = """<packageSource key="company"><package pattern="*" /></packageSource>""";
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear />{{oldSourceEntry}}{{companySource}}</packageSources>
              <packageSourceMapping>{{oldMapping}}{{companyMapping}}</packageSourceMapping>
            </configuration>
            """);
        var originalParent = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        var target = await WriteConfigAsync(directory, $$"""
            <configuration>
              <packageSources>
                {{(clearSources ? "<clear />" + companySource : "")}}
                {{(locallyDefined ? oldSourceEntry : "")}}
              </packageSources>
              <packageSourceMapping>
                {{(clearMappings ? "<clear />" + companyMapping : "")}}
                {{(locallyDefined ? oldMapping : "")}}
              </packageSourceMapping>
            </configuration>
            """);
        var channel = CreateChannel([new("Aspire*", newSource)]);
        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            directory, channel, createIfMissing: true, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        var document = XDocument.Load(target.FullName);
        var expectedLocalSourceKeys = new List<string> { "aspire-test" };
        if (clearSources)
        {
            expectedLocalSourceKeys.Add("company");
        }
        if (locallyDefined && hasUnrelatedPattern)
        {
            expectedLocalSourceKeys.Add("aspire-staging");
        }
        Assert.Equal(expectedLocalSourceKeys.Order(StringComparer.Ordinal),
            document.Root!.Element("packageSources")!.Elements("add")
                .Select(static source => source.Attribute("key")!.Value).Order(StringComparer.Ordinal));
        var localOldMappings = document.Root.Element("packageSourceMapping")!.Elements("packageSource")
            .Where(static mapping => mapping.Attribute("key")?.Value == "aspire-staging")
            .SelectMany(static mapping => mapping.Elements("package"))
            .Select(static pattern => pattern.Attribute("pattern")!.Value);
        Assert.Equal(locallyDefined && hasUnrelatedPattern ? ["Private.*"] : [], localOldMappings);
        var inheritsOldMapping = !clearMappings && !(locallyDefined && hasUnrelatedPattern);
        string[] expectedAspireSources = !inheritsOldMapping ? [newSource] : clearSources ? [] : [oldSource];
        Assert.Equal(expectedAspireSources,
            NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Aspire.Hosting.Redis"));

        Assert.Equal(originalParent, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, null)]
    [InlineData(false, "aspire-staging", "true")]
    [InlineData(true, "aspire-staging", "true")]
    [InlineData(false, "aspire-staging", "false")]
    [InlineData(true, "aspire-staging", "false")]
    [InlineData(false, "ASPIRE-STAGING", "true")]
    [InlineData(true, "ASPIRE-STAGING", "true")]
    public async Task CreatePersistentNuGetConfigContentAsync_StableCleanupRemovesLocalRecordsRegardlessOfParent(
        bool hasMappings, string? disabledKey, string? disabledValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var disabled = disabledKey is null ? "" : $"""<disabledPackageSources><add key="{disabledKey}" value="{disabledValue}" /></disabledPackageSources>""";
        const string oldSource = """<add key="aspire-staging" value="https://old.example/v3/index.json" />""";
        const string oldMapping = """<packageSource key="aspire-staging"><package pattern="Aspire.Hosting.Redis" /></packageSource>""";
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear />{{oldSource}}</packageSources>
              {{disabled}}
              {{(hasMappings ? "<packageSourceMapping>" + oldMapping + "</packageSourceMapping>" : "")}}
            </configuration>
            """);
        var parentBytes = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        var target = await WriteConfigAsync(directory, $$"""
            <configuration>
              <packageSources>
                {{oldSource}}
                <add key="user" value="https://private.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources><add key="future-private" value="true" /></disabledPackageSources>
              {{(hasMappings ? "<packageSourceMapping>" + oldMapping + """<packageSource key="user"><package pattern="User.*" /></packageSource></packageSourceMapping>""" : "")}}
            </configuration>
            """);
        var originalDisabled = XDocument.Load(target.FullName).Root!.Element("disabledPackageSources")!.ToString();
        var channel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable, PackageChannelQuality.Stable, null,
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        var original = await File.ReadAllBytesAsync(target.FullName);
        var service = NuGetTestHelper.CreateService();
        var configuration = service.BuildChannelConfiguration(
            directory, "test", channel, null, null, TestContext.Current.CancellationToken);

        var content = await new DotNetAppHostNuGetConfigMerger(service).CreatePersistentNuGetConfigContentAsync(
            configuration, target, original, globalPackagesFolder: null, TestContext.Current.CancellationToken);

        using var stream = new MemoryStream(content);
        var document = XDocument.Load(stream);
        Assert.Equal(["user"], document.Root!.Element("packageSources")!.Elements("add")
            .Select(static source => source.Attribute("key")!.Value));
        Assert.Equal(hasMappings ? ["user"] : [],
            document.Root.Element("packageSourceMapping")?.Elements("packageSource")
                .Select(static mapping => mapping.Attribute("key")!.Value) ?? []);
        Assert.Equal(originalDisabled, document.Root.Element("disabledPackageSources")!.ToString());
        Assert.Equal(parentBytes, await File.ReadAllBytesAsync(parent.FullName));
        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
    }

    [Fact]
    public async Task PrepareAsync_InheritedOldMappingDoesNotBlockByteIdenticalNoOp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources><clear /><add key="aspire-staging" value="https://old.example/v3/index.json" /></packageSources>
              <packageSourceMapping>
                <packageSource key="aspire-staging"><package pattern="Aspire.Hosting.Redis" /><package pattern="Private.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var directory = workspace.CreateDirectory("apphost");
        var target = await WriteConfigAsync(directory, """
            <configuration>
              <packageSources><add key="aspire-test" value="https://selected.example/v3/index.json" /></packageSources>
              <packageSourceMapping><packageSource key="aspire-test"><package pattern="Aspire*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        var originalParent = await File.ReadAllBytesAsync(parent.FullName);
        var original = await File.ReadAllBytesAsync(target.FullName);

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            directory, CreateChannel([new("Aspire*", "https://selected.example/v3/index.json")]),
            createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.Null(candidate);
        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
        Assert.Equal(originalParent, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Fact]
    public async Task PrepareAsync_AlreadyDisabledUnmappedInheritedSourceDoesNotConflictWithNewChannel()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources>
                <clear />
                <add key="aspire-staging" value="https://old.example/v3/index.json" />
                <add key="company" value="https://company.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources><add key="aspire-staging" value="true" /></disabledPackageSources>
              <packageSourceMapping><packageSource key="company"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        var originalParent = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        await WriteConfigAsync(directory, "<configuration />");
        const string source = "https://new.example/v3/index.json";

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            directory, CreateChannel([new("Aspire*", source)]), createIfMissing: true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Aspire.Hosting.Redis"));
        Assert.Equal(["aspire-staging"], NuGetTestHelper.CreateClient().GetSettings(
            directory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]).DisabledPackageSourceKeys);
        Assert.Equal(originalParent, await File.ReadAllBytesAsync(parent.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_DiscoveryOnlyChangesDoNotRequireOwnershipOfUnchangedUserMapping(bool requiresEdit)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string source = "https://selected.example/v3/index.json";
        const string privateSource = "https://private.example/v3/index.json";
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="company" value="{{source}}" />
                <add key="private" value="{{privateSource}}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="company"><package pattern="{{(requiresEdit ? "Company.*" : "Aspire*")}}" /></packageSource>
                <packageSource key="private"><package pattern="Aspire.Hosting.Redis" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(target.FullName);
        var service = NuGetTestHelper.CreateService();
        var configuration = service.BuildAppHostConfiguration(
            workspace.WorkspaceRoot, "test", [new("Aspire*", source)], TestContext.Current.CancellationToken);
        Assert.True(configuration.HasSourcePolicyChanges);
        var merger = new DotNetAppHostNuGetConfigMerger(service);

        if (requiresEdit)
        {
            await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                merger.PrepareAsync(workspace.WorkspaceRoot, configuration, createIfMissing: true,
                    globalPackagesFolder: null, TestContext.Current.CancellationToken));
        }
        else
        {
            var candidate = await merger.PrepareAsync(
                workspace.WorkspaceRoot, configuration, createIfMissing: true, globalPackagesFolder: null,
                TestContext.Current.CancellationToken);
            Assert.Null(candidate);
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
        Assert.Equal([privateSource], NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Aspire.Hosting.Redis"));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-old/nuget/v3/index.json")]
    [InlineData("./.aspire/hives/old/packages")]
    public async Task PrepareAsync_AlreadyDisabledInheritedFeedDoesNotTriggerRepeatedChanges(string retiredSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="retired" value="{{retiredSource}}" />
                <add key="company" value="https://feed.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="retired" value="true" />
              </disabledPackageSources>
            </configuration>
            """);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var target = await WriteConfigAsync(appHostDirectory, """
            <configuration>
              <packageSourceMapping>
                <clear />
                <packageSource key="company"><package pattern="Aspire*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(target.FullName);

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            appHostDirectory, CreateChannel([new("Aspire*", "https://feed.example/v3/index.json")]),
            createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.Null(candidate);
        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrepareAsync_PreservesDisabledSourcesWithoutNormalization(bool hasLocalSection, bool clearDisabled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="company" value="https://company.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="nuget.org" value="true" />
                <add key="parent-only" value="true" />
              </disabledPackageSources>
              <packageSourceMapping><packageSource key="company"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        var originalParent = await File.ReadAllBytesAsync(parent.FullName);
        var directory = workspace.CreateDirectory("apphost");
        var disabledSection = hasLocalSection
            ? $$"""
              <disabledPackageSources>
                <!-- Keep explicit values and aliases even after their source is removed. -->
                {{(clearDisabled ? "<clear />" : "")}}
                <add key="nuget.org" value="true" />
                <add key="aspire-staging" value="false" />
                <add key="aspire-apphost-0123456789abcdef" value="true" />
              </disabledPackageSources>
              """
            : string.Empty;
        var target = await WriteConfigAsync(directory, $$"""
            <configuration>
              <packageSources><add key="aspire-staging" value="https://old.example/v3/index.json" /></packageSources>
              {{disabledSection}}
              <packageSourceMapping>
                <clear />
                <packageSource key="aspire-staging"><package pattern="Aspire*" /></packageSource>
                <packageSource key="company"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var originalDisabled = XDocument.Load(target.FullName).Root!.Element("disabledPackageSources")?.ToString();
        const string source = "https://new.example/v3/index.json";

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            directory, CreateChannel([new("Aspire*", source), new("*", PackageSources.NuGetOrg)]),
            createIfMissing: true, TestContext.Current.CancellationToken);

        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        var proposed = XDocument.Load(target.FullName);
        Assert.Equal(originalDisabled, proposed.Root!.Element("disabledPackageSources")?.ToString());
        var settings = NuGetTestHelper.CreateClient().GetSettings(
            directory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.False(Assert.Single(settings.Sources, static item => item.Name == "nuget.org").IsEnabled);
        Assert.Equal([source], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Aspire.Hosting.Redis"));
        Assert.Equal(originalParent, await File.ReadAllBytesAsync(parent.FullName));
        await Verify(proposed.ToString(), "xml").UseParameters(hasLocalSection, clearDisabled);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PrepareAsync_PersistencePreservesDisabledSourcesWhenRetiringLocalSources(
        bool locallyDefined, bool inheritedDefined, bool clearSources)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        const string retired = """<add key="retired" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-old/nuget/v3/index.json" />""";
        const string userSources = """
            <add key="company" value="https://company.example/v3/index.json" />
            <add key="user-disabled" value="https://private.example/v3/index.json" />
            """;
        var parent = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources><clear />{{userSources}}{{(inheritedDefined ? retired : "")}}</packageSources>
              <disabledPackageSources><add key="user-disabled" value="true" /></disabledPackageSources>
            </configuration>
            """);
        var parentBytes = await File.ReadAllBytesAsync(parent.FullName);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var target = await WriteConfigAsync(appHostDirectory, $$"""
            <configuration>
              <packageSources>{{(clearSources ? "<clear />" + userSources : "")}}{{(locallyDefined ? retired : "")}}</packageSources>
              <disabledPackageSources>
                <clear />
                <add key="user-disabled" value="true" />
                <add key="future-private" value="true" />
                <add key="aspire-apphost-0e77e1652908bf0e" value="true" />
                <add key="aspire-apphost-efc407738a41fb53-0" value="true" />
              </disabledPackageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="retired"><package pattern="Aspire*" /></packageSource>
                <packageSource key="company"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            appHostDirectory, CreateChannel([new("Aspire*", "https://new.example/v3/index.json")]),
            createIfMissing: true, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);

        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            appHostDirectory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.Equal(
            ["aspire-apphost-0e77e1652908bf0e", "aspire-apphost-efc407738a41fb53-0", "future-private", "user-disabled"],
            snapshot.DisabledPackageSourceKeys.Order(StringComparer.Ordinal));
        Assert.Equal(["https://new.example/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting.Redis"));
        Assert.Equal(parentBytes, await File.ReadAllBytesAsync(parent.FullName));
        await Verify(XDocument.Load(target.FullName).ToString(), "xml")
            .UseParameters(locallyDefined, inheritedDefined, clearSources);
    }

    [Fact]
    public async Task PrepareAsync_ChannelRoundTripDoesNotAccumulateProjectionMasks()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources><clear /><add key="company" value="https://company.example/v3/index.json" /></packageSources>
            </configuration>
            """);
        var service = NuGetTestHelper.CreateService();
        const string prSource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-pr-19763/nuget/v3/index.json";
        const string dailySource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";
        foreach (var source in new[] { prSource, dailySource, prSource, dailySource, prSource })
        {
            var configuration = service.BuildAppHostConfiguration(
                workspace.WorkspaceRoot, "apphost-0123456789abcdef", [new("Aspire*", source)],
                cancellationToken: TestContext.Current.CancellationToken);
            using var projection = await service.CreateConfigurationPreviewAsync(
                workspace.WorkspaceRoot, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
            Assert.Equal([source],
                NuGetTestHelper.GetEligiblePackageSources(projection.EffectiveWorkingDirectory.FullName, "Aspire.Hosting.Redis"));
            var candidate = await new DotNetAppHostNuGetConfigMerger(service).PrepareAsync(
                workspace.WorkspaceRoot, configuration, createIfMissing: true,
                globalPackagesFolder: null, TestContext.Current.CancellationToken);
            Assert.NotNull(candidate);
            await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
            Assert.Empty(service.GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken).DisabledPackageSourceKeys);
            Assert.Equal(new[] { source, "https://company.example/v3/index.json" }.Order(StringComparer.Ordinal),
                NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Aspire.Hosting.Redis"));
        }

        await Verify(XDocument.Load(target.FullName).ToString(), "xml");
    }

    [Fact]
    public async Task PrepareAsync_RefusesReenablingInheritedChannelSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var parentFile = await WriteConfigAsync(workspace.WorkspaceRoot, """
            <configuration>
              <packageSources>
                <clear />
                <add key="selected" value="https://selected.example/v3/index.json" />
                <add key="user-disabled" value="https://private.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="selected" value="true" />
                <add key="user-disabled" value="true" />
                <add key="future-private" value="true" />
              </disabledPackageSources>
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(parentFile.FullName);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
                appHostDirectory, CreateChannel([new("Aspire*", "https://selected.example/v3/index.json")]),
                createIfMissing: true, TestContext.Current.CancellationToken));

        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            appHostDirectory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.Equal(["future-private", "selected", "user-disabled"], snapshot.DisabledPackageSourceKeys.Order(StringComparer.Ordinal));
        Assert.Empty(NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting.Redis"));
        Assert.Empty(appHostDirectory.EnumerateFiles());
        Assert.Equal(original, await File.ReadAllBytesAsync(parentFile.FullName));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(".nugetpackages", false)]
    [InlineData("company-cache", false)]
    public async Task PrepareAsync_EquivalentSourcePolicyStillEvaluatesCacheFolder(
        string? existingFolder, bool expectedCandidate)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var cacheSetting = existingFolder is null
            ? string.Empty
            : $"""<config><add key="globalPackagesFolder" value="{existingFolder}" /></config>""";
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="aspire-test" value="https://feed.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="aspire-test">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
              {{cacheSetting}}
            </configuration>
            """);
        var original = await File.ReadAllBytesAsync(target.FullName);

        var candidate = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            workspace.WorkspaceRoot, [new("Aspire*", "https://feed.example/v3/index.json")],
            createIfMissing: true, configureGlobalPackagesFolder: true, TestContext.Current.CancellationToken);

        Assert.Equal(expectedCandidate, candidate is not null);
        if (candidate is not null)
        {
            Assert.Equal(CliPathHelper.StagingNuGetPackagesFolderName,
                candidate.GetProposedDocument().SelectSingleNode("/configuration/config/add[@key='globalPackagesFolder']/@value")!.Value);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(target.FullName));
    }

    [Fact]
    public async Task ApplyAsync_RefusesChangedConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var target = await WriteConfigAsync(workspace.WorkspaceRoot, "<configuration />");
        var channel = CreateChannel([new PackageMapping("Aspire*", "https://feed.example/v3/index.json")]);
        var update = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            workspace.WorkspaceRoot, channel, createIfMissing: true, TestContext.Current.CancellationToken);
        Assert.NotNull(update);
        const string changedContent = "<configuration><!-- concurrent edit --></configuration>";
        await File.WriteAllTextAsync(target.FullName, changedContent);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DotNetAppHostNuGetConfigMerger.ApplyAsync(update, TestContext.Current.CancellationToken));

        Assert.Contains(target.FullName, exception.Message);
        Assert.Equal(changedContent, await File.ReadAllTextAsync(target.FullName));
    }

    [Fact]
    public async Task ApplyAsync_RefusesConfigCreatedAfterPreparation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var channel = CreateChannel([new PackageMapping("Aspire*", "https://feed.example/v3/index.json")]);
        var update = await DotNetAppHostNuGetConfigTestHelper.PrepareAsync(
            workspace.WorkspaceRoot, channel, createIfMissing: true, TestContext.Current.CancellationToken);
        Assert.NotNull(update);
        const string createdContent = "<configuration><!-- concurrent creation --></configuration>";
        await File.WriteAllTextAsync(update.TargetFile.FullName, createdContent);

        await Assert.ThrowsAsync<IOException>(
            () => DotNetAppHostNuGetConfigMerger.ApplyAsync(update, TestContext.Current.CancellationToken));

        Assert.Equal(createdContent, await File.ReadAllTextAsync(update.TargetFile.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateOrUpdateAsync_ConfirmsCompleteGlobalPackagesFolderChange(bool existingConfig)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        if (existingConfig)
        {
            await WriteConfigAsync(workspace.WorkspaceRoot, "<configuration />");
        }

        XmlDocument? confirmedDocument = null;
        await DotNetAppHostNuGetConfigTestHelper.CreateOrUpdateAsync(
            workspace.WorkspaceRoot,
            [new PackageMapping("Aspire*", "https://feed.example/v3/index.json")],
            configureGlobalPackagesFolder: true,
            confirmationCallback: (_, _, proposed, _) =>
            {
                confirmedDocument = proposed;
                Assert.Equal(
                    CliPathHelper.StagingNuGetPackagesFolderName,
                    proposed.SelectSingleNode("/configuration/config/add[@key='globalPackagesFolder']/@value")!.Value);
                return Task.FromResult(true);
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(confirmedDocument);
        Assert.Equal(
            XDocument.Parse(confirmedDocument.OuterXml).ToString(),
            XDocument.Load(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")).ToString());
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");
}
