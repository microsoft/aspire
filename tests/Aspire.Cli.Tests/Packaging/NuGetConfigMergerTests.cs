// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using System.Xml;
using Aspire.Cli.Packaging;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Packaging;

public class NuGetConfigMergerTests
{
    private readonly ITestOutputHelper _outputHelper;

    public NuGetConfigMergerTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    private static async Task<FileInfo> WriteConfigAsync(DirectoryInfo dir, string content)
    {
        var path = Path.Combine(dir.FullName, "nuget.config");
        await File.WriteAllTextAsync(path, content);
        return new FileInfo(path);
    }

    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = prerelease; _ = nugetConfigFile; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = prerelease; _ = nugetConfigFile; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = prerelease; _ = nugetConfigFile; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = packageId; _ = filter; _ = prerelease; _ = nugetConfigFile; _ = useCache; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
    }

    private static PackageChannel CreateChannel(PackageMapping[] mappings) => PackageChannel.CreateExplicitChannel("test", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache());

    [Fact]
    public async Task CreateOrUpdateAsync_CreatesConfigFromMappings_WhenNoExistingConfig()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping(PackageMapping.AllPackages, "https://feed2.example")
        };

    var channel = CreateChannel(mappings);
    await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));

    using var tempConfig = await TemporaryNuGetConfig.CreateAsync(mappings);
    var expected = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var actual = await File.ReadAllTextAsync(targetConfigPath);
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_GeneratesConfigFromMappings_WhenChannelProvided()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping(PackageMapping.AllPackages, "https://feed2.example")
        };

    var channel = CreateChannel(mappings);
    await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));

        var xml = XDocument.Load(targetConfigPath);
        var packageSources = xml.Root!.Element("packageSources")!;
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://feed1.example");
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://feed2.example");

        var psm = xml.Root!.Element("packageSourceMapping");
        Assert.NotNull(psm);
        Assert.Equal(2, psm!.Elements("packageSource").Count());
    }

    [Fact]
    public async Task CreateOrUpdateAsync_AddsMissingSources_WhenUpdatingExistingConfig()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config with one source only
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="https://feed1.example" value="https://feed1.example" />
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
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("Microsoft.*", "https://feed2.example") // feed2 missing
        };

    var channel = CreateChannel(mappings);
    await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://feed2.example");

        // Ensure existing mapping retained
        var psm = xml.Root!.Element("packageSourceMapping")!;
        Assert.NotNull(psm.Elements("packageSource").First().Elements("package").FirstOrDefault(p => (string?)p.Attribute("pattern") == "Aspire.*"));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RemapsPatternsAndRemovesEmptySources()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
    await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        // Old source should be removed because it's no longer used
        Assert.DoesNotContain(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://old.example");
        // New source should be present
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://new.example");

        var psm = xml.Root!.Element("packageSourceMapping")!;
        Assert.Single(psm.Elements("packageSource"));
        Assert.Equal("https://new.example", (string?)psm.Element("packageSource")!.Attribute("key"));
        Assert.Equal("Lib.*", (string?)psm.Element("packageSource")!.Element("package")!.Attribute("pattern"));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CreatesPackageSourceMapping_WhenAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;

        // Existing config without packageSourceMapping
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="https://feed1.example" value="https://feed1.example" />
                    <add key="https://feed2.example" value="https://feed2.example" />
                </packageSources>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example"),
            new PackageMapping("Microsoft.*", "https://feed2.example")
        };

    var channel = CreateChannel(mappings);
    await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var psm = xml.Root!.Element("packageSourceMapping");
        Assert.NotNull(psm);
        Assert.Equal(2, psm!.Elements("packageSource").Count());
    }

    [Fact]
    public void HasMissingSources_ReturnsTrue_WhenConfigAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;
    var mappings = new[] { new PackageMapping("Aspire.*", "https://feed.example") };
    var channel = CreateChannel(mappings);
    Assert.True(NuGetConfigMerger.HasMissingSources(root, channel));
    }

    [Fact]
    public async Task HasMissingSources_ReturnsTrue_WhenPatternMappedToWrongSource()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
    Assert.True(NuGetConfigMerger.HasMissingSources(root, channel));
    }

    [Fact]
    public async Task HasMissingSources_ReturnsFalse_WhenAllSourcesAndMappingsPresent()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
    Assert.False(NuGetConfigMerger.HasMissingSources(root, channel));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_ReusesExistingSourceKeys_WhenMappingToExistingSourcesByUrl()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
            new PackageMapping("*", "https://api.nuget.org/v3/index.json") // Should map to existing "nuget" key
        };

        var channel = CreateChannel(mappings);
        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        
        // Existing sources should still be present with their original keys
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "nuget" && (string?)e.Attribute("value") == "https://api.nuget.org/v3/index.json");
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "dotnet9" && (string?)e.Attribute("value") == "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json");

        // New source should be added
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://example.com/aspire-feed");

        // Package source mapping should use existing key "nuget" instead of URL
        var psm = xml.Root!.Element("packageSourceMapping")!;
        var nugetMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "nuget");
        Assert.NotNull(nugetMapping);
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");

        // Should NOT create a mapping with the URL as key when existing key exists
        var urlMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "https://api.nuget.org/v3/index.json");
        Assert.Null(urlMapping);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PreservesAllExistingSources_WhenCreatingPackageSourceMappingForFirstTime()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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

        // aspire update adds specific mappings but doesn't include a wildcard
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://example.com/aspire-daily")
        };

        var channel = CreateChannel(mappings);
        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        
        // All original sources should still be present
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "nuget.org");
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("key") == "custom");

        // New aspire source should be added
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == "https://example.com/aspire-daily");

        // Debug: Print the XML to understand what's happening
        _outputHelper.WriteLine("Generated XML:");
        _outputHelper.WriteLine(xml.ToString());

        // Package source mapping should preserve the original behavior:
        // Since the original config had NO packageSourceMapping, all existing sources should get "*" patterns
        // so they can continue to serve packages
        var psm = xml.Root!.Element("packageSourceMapping")!;
        
        // The aspire source should have its specific pattern
        var aspireMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "https://example.com/aspire-daily");
        Assert.NotNull(aspireMapping);
        Assert.Contains(aspireMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Aspire*");

        // The existing sources should get wildcard patterns to preserve their original functionality
        var nugetMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "nuget.org");
        Assert.NotNull(nugetMapping);
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");

        var customMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "custom");
        Assert.NotNull(customMapping);
        Assert.Contains(customMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_AddsSpecificMappings_WhenExistingWildcardMappingPresent()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

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
        var aspireMapping = psm.Elements("packageSource").FirstOrDefault(ps => (string?)ps.Attribute("key") == "https://example.com/aspire-daily");
        Assert.NotNull(aspireMapping);
        Assert.Contains(aspireMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Aspire*");
        Assert.Contains(aspireMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Microsoft.Extensions.ServiceDiscovery*");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_RemovesUnrequiredSources_InsteadOfAddingWildcardPattern()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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

        // New mappings that remap Aspire patterns to nuget.org and add a wildcard
        var mappings = new[]
        {
            new PackageMapping("Aspire*", "https://api.nuget.org/v3/index.json"),
            new PackageMapping("Microsoft.Extensions.ServiceDiscovery*", "https://api.nuget.org/v3/index.json"),
            new PackageMapping("*", "https://api.nuget.org/v3/index.json")
        };

        var channel = CreateChannel(mappings);
        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var xml = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var packageSources = xml.Root!.Element("packageSources")!;
        
        // The PR hive source should be removed because it's safe to remove and no longer needed
        Assert.DoesNotContain(packageSources.Elements("add"), 
            e => (string?)e.Attribute("value") == "C:\\Users\\user\\.aspire\\hives\\invalid-pr");
        
        // The user-defined source should be preserved even though its patterns were remapped
        Assert.Contains(packageSources.Elements("add"), 
            e => (string?)e.Attribute("value") == "https://valid.example");
        
        // NuGet.org should be added for all the patterns
        Assert.Contains(packageSources.Elements("add"), 
            e => (string?)e.Attribute("value") == "https://api.nuget.org/v3/index.json");

        var psm = xml.Root!.Element("packageSourceMapping")!;
        
        // The PR hive source should not have any mapping entries (removed entirely)
        Assert.DoesNotContain(psm.Elements("packageSource"), 
            ps => (string?)ps.Attribute("key") == "C:\\Users\\user\\.aspire\\hives\\invalid-pr");
        
        // The user-defined source should get a wildcard pattern to remain functional
        var validExampleMapping = psm.Elements("packageSource")
            .FirstOrDefault(ps => (string?)ps.Attribute("key") == "https://valid.example");
        Assert.NotNull(validExampleMapping);
        Assert.Contains(validExampleMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");
        
        // NuGet.org should have all the patterns
        var nugetMapping = psm.Elements("packageSource")
            .FirstOrDefault(ps => (string?)ps.Attribute("key") == "https://api.nuget.org/v3/index.json");
        Assert.NotNull(nugetMapping);
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Aspire*");
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "Microsoft.Extensions.ServiceDiscovery*");
        Assert.Contains(nugetMapping.Elements("package"), p => (string?)p.Attribute("pattern") == "*");
        
        // There should be two packageSource elements (nuget.org and valid.example)
        Assert.Equal(2, psm.Elements("packageSource").Count());
    }

    [Fact]
    public async Task CreateOrUpdateAsync_CallbackInvokedForNewConfig()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
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
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);
        
        bool callbackInvoked = false;

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
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
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
        
        await WriteConfigAsync(root, existingConfig);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);
        
        bool callbackInvoked = false;
        FileInfo? callbackTargetFile = null;
        XmlDocument? callbackOriginalContent = null;
        XmlDocument? callbackProposedContent = null;

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
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
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
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
        
        await WriteConfigAsync(root, existingConfig);
        var originalContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "nuget.config"));

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);
        
        bool callbackInvoked = false;

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel, (targetFile, originalContent, proposedContent, cancellationToken) =>
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
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var root = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://feed1.example")
        };

        var channel = CreateChannel(mappings);
        
        // Call without callback - should work as before
        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        // Verify file was created
        var targetConfigPath = Path.Combine(root.FullName, "nuget.config");
        Assert.True(File.Exists(targetConfigPath));
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");
}
