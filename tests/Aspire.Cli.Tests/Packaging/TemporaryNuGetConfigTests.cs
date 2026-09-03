// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Packaging;

public class TemporaryNuGetConfigTests
{
    private readonly ITestOutputHelper _outputHelper;

    public TemporaryNuGetConfigTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    [Fact]
    public async Task CreateAsync_IncludesAllPackageSourceMappings()
    {
        // Arrange
        var mappings = new PackageMapping[]
        {
            new("Aspire.*", "https://example.com/feed1"),
            new(PackageMapping.AllPackages, "https://example.com/feed2"), // "*" filter
            new("Microsoft.*", "https://example.com/feed1")
        };

        // Act
        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(mappings);

        // Assert
        var configContent = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(configContent);

        // Verify that package source mappings section exists
        var packageSourceMappingNode = xmlDoc.SelectSingleNode("//packageSourceMapping");
        Assert.NotNull(packageSourceMappingNode);

        // Verify all package sources are present
        var packageSourceNodes = xmlDoc.SelectNodes("//packageSourceMapping/packageSource");
        Assert.NotNull(packageSourceNodes);
        Assert.Equal(2, packageSourceNodes.Count); // Two distinct sources

        // Verify that the AllPackages mapping is included
        var allPackagesMapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='aspire-1']/package[@pattern='*']");
        Assert.NotNull(allPackagesMapping);

        // Verify other specific mappings are also included
        var aspireMapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='aspire-0']/package[@pattern='Aspire.*']");
        Assert.NotNull(aspireMapping);

        var microsoftMapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='aspire-0']/package[@pattern='Microsoft.*']");
        Assert.NotNull(microsoftMapping);
    }

    [Fact]
    public async Task CreateAsync_WithOnlyAllPackagesMappings_IncludesAllMappings()
    {
        // Arrange
        var mappings = new PackageMapping[]
        {
            new(PackageMapping.AllPackages, "https://feed1.example.com"),
            new(PackageMapping.AllPackages, "https://feed2.example.com")
        };

        // Act
        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(mappings);

        // Assert
        var configContent = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(configContent);

        // Verify that package source mappings section exists
        var packageSourceMappingNode = xmlDoc.SelectSingleNode("//packageSourceMapping");
        Assert.NotNull(packageSourceMappingNode);

        // Verify all package sources are present
        var packageSourceNodes = xmlDoc.SelectNodes("//packageSourceMapping/packageSource");
        Assert.NotNull(packageSourceNodes);
        Assert.Equal(2, packageSourceNodes.Count); // Two distinct sources

        // Verify that both AllPackages mappings are included
        var feed1Mapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='aspire-0']/package[@pattern='*']");
        Assert.NotNull(feed1Mapping);

        var feed2Mapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='aspire-1']/package[@pattern='*']");
        Assert.NotNull(feed2Mapping);
    }

    [Fact]
    public async Task CreateAsync_WithNoMappings_CreatesValidConfig()
    {
        // Arrange
        var mappings = Array.Empty<PackageMapping>();

        // Act
        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(mappings);

        // Assert
        var configContent = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(configContent);

        // Verify basic structure exists
        var configNode = xmlDoc.SelectSingleNode("//configuration");
        Assert.NotNull(configNode);

        var packageSourcesNode = xmlDoc.SelectSingleNode("//packageSources");
        Assert.NotNull(packageSourcesNode);

        // No package source mappings should exist when no mappings provided
        var packageSourceMappingNode = xmlDoc.SelectSingleNode("//packageSourceMapping");
        Assert.Null(packageSourceMappingNode);
    }

    [Fact]
    public async Task CreateAsync_WithConfiguredGlobalPackagesFolder_AddsConfigEntry()
    {
        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(
            [new PackageMapping("Aspire.*", "https://example.com/feed")],
            configureGlobalPackagesFolder: true);

        var configContent = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(configContent);

        var globalPackagesFolder = xmlDoc.SelectSingleNode("//config/add[@key='globalPackagesFolder']");
        Assert.NotNull(globalPackagesFolder);
        Assert.Equal(".nugetpackages", globalPackagesFolder!.Attributes!["value"]!.Value);
    }

    [Fact]
    public async Task CreateAsync_WithExplicitGlobalPackagesFolderOverride_UsesOverrideValue()
    {
        // Callers that need the cache to outlive the temp config (e.g. PrebuiltAppHostServer's
        // staging path) supply an absolute, persistent path so BundleNuGetService manifest paths
        // remain valid after TemporaryNuGetConfig.Dispose deletes the temp directory.
        var overrideValue = Path.Combine(Path.GetTempPath(), "aspire-tests", "stable-cache", "deadbeef");

        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(
            [new PackageMapping("Aspire.*", "https://example.com/feed")],
            configureGlobalPackagesFolder: true,
            globalPackagesFolderValue: overrideValue);

        var configContent = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(configContent);

        var globalPackagesFolder = xmlDoc.SelectSingleNode("//config/add[@key='globalPackagesFolder']");
        Assert.NotNull(globalPackagesFolder);
        Assert.Equal(overrideValue, globalPackagesFolder!.Attributes!["value"]!.Value);
    }

    [Fact]
    public async Task CreateAsync_WithoutConfiguredGlobalPackagesFolder_IgnoresOverride()
    {
        // When configureGlobalPackagesFolder is false the override is irrelevant — no
        // <config><add key="globalPackagesFolder"/> element should be emitted at all.
        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(
            [new PackageMapping("Aspire.*", "https://example.com/feed")],
            configureGlobalPackagesFolder: false,
            globalPackagesFolderValue: "/should/not/appear");

        var configContent = await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName);
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(configContent);

        Assert.Null(xmlDoc.SelectSingleNode("//config/add[@key='globalPackagesFolder']"));
    }

    [Theory]
    [InlineData("https://example.com/feed")]
    [InlineData("/var/folders/X/hives/pr-17105/packages")]
    [InlineData(@"C:\Users\X\.aspire\hives\pr-17105\packages")]
    public async Task CreateAsync_PackageSourceAddKeyMatchesPackageSourceMappingKey(string source)
    {
        // Bug B defense: NuGet's packageSourceMapping lookup matches the
        // <packageSource key="..."> attribute against the source name registered
        // from <packageSources><add key="..." />. A future refactor that splits
        // those keys (or canonicalizes one side and not the other) would silently
        // drop the mapping. This invariant lives at the writer; pin it.
        //
        // Note that we ALSO need the source written here to be in the form NuGet
        // will accept after its own internal canonicalization (e.g. on macOS the
        // upstream caller must strip /private/var → /var before constructing the
        // PackageMapping — see CliPathHelper.StripMacOSFirmlinkPrefix and the
        // GetAspireHomeDirectory_OnMacOS_PrRouteWithFirmlinkedProcessPath test).
        // This test only pins the writer's symmetry contract.
        var mappings = new PackageMapping[]
        {
            new("Aspire*", source),
            new(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json"),
        };

        using var tempConfig = await TemporaryNuGetConfig.CreateAsync(mappings);

        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(await File.ReadAllTextAsync(tempConfig.ConfigFile.FullName));

        // Collect <packageSources><add key="X" value="Y" /> entries (filter out <clear/>).
        var addNodes = xmlDoc.SelectNodes("//packageSources/add")!;
        var addKeys = new List<string>();
        foreach (XmlNode add in addNodes)
        {
            addKeys.Add(add.Attributes!["key"]!.Value);
        }

        // Collect <packageSourceMapping><packageSource key="X"> entries.
        var mappingNodes = xmlDoc.SelectNodes("//packageSourceMapping/packageSource")!;
        var mappingKeys = new List<string>();
        foreach (XmlNode m in mappingNodes)
        {
            mappingKeys.Add(m.Attributes!["key"]!.Value);
        }

        // Every mapping key must have a matching <add key>, byte-for-byte.
        foreach (var mappingKey in mappingKeys)
        {
            Assert.Contains(mappingKey, addKeys);
        }

        var sourceKey = addNodes
            .Cast<XmlNode>()
            .Single(add => add.Attributes!["value"]!.Value == source)
            .Attributes!["key"]!
            .Value;
        Assert.Contains(sourceKey, mappingKeys);
    }

    [Fact]
    public async Task CreateAsync_PreservesCaseSensitiveSourcePaths()
    {
        using var config = await TemporaryNuGetConfig.CreateAsync(
        [
            new PackageMapping("Upper.*", "https://example.com/Feed/index.json"),
            new PackageMapping("Lower.*", "https://example.com/feed/index.json")
        ]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var sourcesByKey = document.Descendants("packageSources")
            .Elements("add")
            .ToDictionary(
                static element => element.Attribute("key")!.Value,
                static element => element.Attribute("value")!.Value,
                StringComparer.OrdinalIgnoreCase);
        var mappingsBySource = document.Descendants("packageSourceMapping")
            .Elements("packageSource")
            .ToDictionary(
                element => sourcesByKey[element.Attribute("key")!.Value],
                element => element.Elements("package").Single().Attribute("pattern")!.Value,
                PackageSourceIdentity.Comparer);

        Assert.Equal("Upper.*", mappingsBySource["https://example.com/Feed/index.json"]);
        Assert.Equal("Lower.*", mappingsBySource["https://example.com/feed/index.json"]);
    }

    [Fact]
    public async Task CreateComposedAsync_MergesAmbientHierarchyAndAppliesMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var userConfigDirectory = workspace.CreateDirectory("user");
        var userConfigPath = Path.Combine(userConfigDirectory.FullName, "NuGet.Config");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        await File.WriteAllTextAsync(userConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="private" value="./packages" />
                <add key="daily" value="{{channelSource}}" />
              </packageSources>
              <disabledPackageSources>
                <add key="daily" value="true" />
              </disabledPackageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Contoso.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var workspaceConfigDirectory = workspace.CreateDirectory("repo");
        var workspaceConfigPath = Path.Combine(workspaceConfigDirectory.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(workspaceConfigPath, """
            <configuration>
              <packageSources>
                <add key="workspace" value="https://packages.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [workspaceConfigPath, userConfigPath],
            [new PackageMapping("Aspire*", channelSource)]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var packageSources = document.Descendants("packageSources").Elements("add").ToArray();
        Assert.Contains(packageSources, element =>
            element.Attribute("key")?.Value == "private" &&
            element.Attribute("value")?.Value == Path.Combine(userConfigDirectory.FullName, "packages"));
        Assert.Contains(packageSources, element => element.Attribute("key")?.Value == "workspace");
        Assert.Contains(packageSources, element => element.Attribute("value")?.Value == channelSource);
        Assert.NotNull(document.Descendants("packageSourceCredentials").Single().Element("private"));

        var mappings = document.Descendants("packageSourceMapping").Elements("packageSource").ToArray();
        Assert.Contains(mappings, element =>
            element.Elements("package").Any(package => package.Attribute("pattern")?.Value == "Aspire*") &&
            element.Attribute("key")?.Value == "daily");
        Assert.Contains(mappings, element =>
            element.Elements("package").Any(package => package.Attribute("pattern")?.Value == "Contoso.*") &&
            element.Attribute("key")?.Value == "private");
        Assert.Empty(document.Descendants("disabledPackageSources").Elements("add"));
        Assert.True(config.ContainsCredentialMaterial);
    }

    [Fact]
    public async Task CreateComposedAsync_MapsSamePatternToMultipleSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(configPath, """
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

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [configPath],
            [
                new PackageMapping("*", "https://feed1.example"),
                new PackageMapping("*", "https://feed2.example")
            ]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var mappedSources = document.Descendants("packageSourceMapping")
            .Elements("packageSource")
            .Where(element => element.Elements("package").Any(package => package.Attribute("pattern")?.Value == "*"))
            .Select(element => element.Attribute("key")!.Value)
            .ToArray();

        Assert.Equal(["feed1", "feed2"], mappedSources);
    }

    [Fact]
    public async Task CreateComposedAsync_PreservesCaseSensitiveSourcePaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(configPath, "<configuration />");

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [configPath],
            [
                new PackageMapping("Upper.*", "https://example.com/Feed/index.json"),
                new PackageMapping("Lower.*", "https://example.com/feed/index.json")
            ]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var sourcesByKey = document.Descendants("packageSources")
            .Elements("add")
            .ToDictionary(
                static element => element.Attribute("key")!.Value,
                static element => element.Attribute("value")!.Value,
                StringComparer.OrdinalIgnoreCase);
        var mappingsBySource = document.Descendants("packageSourceMapping")
            .Elements("packageSource")
            .ToDictionary(
                element => sourcesByKey[element.Attribute("key")!.Value],
                element => element.Elements("package").Single().Attribute("pattern")!.Value,
                PackageSourceIdentity.Comparer);

        Assert.Equal("Upper.*", mappingsBySource["https://example.com/Feed/index.json"]);
        Assert.Equal("Lower.*", mappingsBySource["https://example.com/feed/index.json"]);
    }

    [Fact]
    public async Task CreateComposedAsync_CacheIdentityIncludesAmbientSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var firstConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "first.config");
        var secondConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "second.config");
        await File.WriteAllTextAsync(firstConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://example.com/private-a/index.json" />
              </packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(secondConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://example.com/private-b/index.json" />
              </packageSources>
            </configuration>
            """);
        var mappings = new[] { new PackageMapping("Aspire*", "https://example.com/aspire/index.json") };

        using var first = await TemporaryNuGetConfig.CreateComposedAsync([firstConfigPath], mappings);
        using var second = await TemporaryNuGetConfig.CreateComposedAsync([secondConfigPath], mappings);

        Assert.NotEqual(first.CacheIdentity, second.CacheIdentity);
        Assert.NotEqual(
            CliPathHelper.ComputeStagingFeedCacheKey(first.CacheIdentity),
            CliPathHelper.ComputeStagingFeedCacheKey(second.CacheIdentity));
    }

    [Fact]
    public async Task CreateComposedAsync_PreservesFallbackFolderPrecedence()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var machineConfigDirectory = workspace.CreateDirectory("machine");
        var machineConfigPath = Path.Combine(machineConfigDirectory.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(machineConfigPath, """
            <configuration>
              <fallbackPackageFolders>
                <clear />
                <add key="shared" value="./shared-machine" />
                <add key="machine" value="./machine-packages" />
              </fallbackPackageFolders>
            </configuration>
            """);
        var userConfigDirectory = workspace.CreateDirectory("user");
        var userConfigPath = Path.Combine(userConfigDirectory.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(userConfigPath, """
            <configuration>
              <fallbackPackageFolders>
                <add key="user" value="./user-packages" />
                <add key="shared" value="./shared-user" />
              </fallbackPackageFolders>
            </configuration>
            """);
        var workspaceConfigDirectory = workspace.CreateDirectory("repo");
        var workspaceConfigPath = Path.Combine(workspaceConfigDirectory.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(workspaceConfigPath, """
            <configuration>
              <fallbackPackageFolders>
                <add key="workspace" value="./workspace-packages" />
              </fallbackPackageFolders>
            </configuration>
            """);

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [workspaceConfigPath, userConfigPath, machineConfigPath],
            []);

        var fallbackPackageFolders = XDocument.Load(config.ConfigFile.FullName)
            .Descendants("fallbackPackageFolders")
            .Single();
        Assert.Equal("clear", fallbackPackageFolders.Elements().First().Name.LocalName);
        Assert.Equal(
            [
                ("workspace", Path.Combine(workspaceConfigDirectory.FullName, "workspace-packages")),
                ("user", Path.Combine(userConfigDirectory.FullName, "user-packages")),
                ("shared", Path.Combine(userConfigDirectory.FullName, "shared-user")),
                ("machine", Path.Combine(machineConfigDirectory.FullName, "machine-packages"))
            ],
            fallbackPackageFolders.Elements("add")
                .Select(element => (element.Attribute("key")!.Value, element.Attribute("value")!.Value))
                .ToArray());
    }

    [PlatformSpecific(TestPlatforms.Windows)]
    [Fact]
    public void ResolvePathFromOrigin_WithDriveRootRelativePath_UsesOriginDrive()
    {
        var result = NuGetConfigComposer.ResolvePathFromOrigin(@"D:\repo", @"\packages");

        Assert.Equal(@"D:\packages", result);
    }

    [Fact]
    public void ResolvePathFromOrigin_WithEmptyPath_PreservesEmptyPath()
    {
        var result = NuGetConfigComposer.ResolvePathFromOrigin(Path.GetTempPath(), string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateComposedAsync_MoreLocalClearRemovesInheritedSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var userConfigPath = Path.Combine(workspace.CreateDirectory("user").FullName, "NuGet.Config");
        await File.WriteAllTextAsync(userConfigPath, """
            <configuration>
              <packageSources>
                <add key="inherited" value="https://inherited.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        var workspaceConfigPath = Path.Combine(workspace.CreateDirectory("repo").FullName, "NuGet.Config");
        await File.WriteAllTextAsync(workspaceConfigPath, """
            <configuration>
              <packageSources>
                <clear />
                <add key="workspace" value="https://workspace.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [workspaceConfigPath, userConfigPath],
            [new PackageMapping("Aspire*", "https://channel.example.com/v3/index.json")]);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var packageSources = document.Descendants("packageSources").Elements("add").ToArray();
        Assert.Equal(
            ["https://workspace.example.com/v3/index.json", "https://channel.example.com/v3/index.json"],
            packageSources.Select(element => element.Attribute("value")!.Value).ToArray());
    }

    [Fact]
    public async Task CreateComposedAsync_CanonicalizesElementsConsumedByMerger()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var configDirectory = workspace.CreateDirectory("repo");
        var configPath = Path.Combine(configDirectory.FullName, "NuGet.Config");
        const string channelSource = "https://packages.example.com/v3/index.json";
        await File.WriteAllTextAsync(configPath, $$"""
            <configuration>
              <PackageSources>
                <AdD key="channel" value="{{channelSource}}" />
              </PackageSources>
              <PackageSourceMapping>
                <PackageSource key="channel">
                  <Package pattern="Contoso.*" />
                </PackageSource>
              </PackageSourceMapping>
              <Config>
                <AdD key="globalPackagesFolder" value="./packages" />
              </Config>
            </configuration>
            """);

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [configPath],
            [new PackageMapping("Aspire*", channelSource)],
            configureGlobalPackagesFolder: true,
            globalPackagesFolderValue: Path.Combine(workspace.WorkspaceRoot.FullName, "unused"));

        var configuration = XDocument.Load(config.ConfigFile.FullName).Root!;
        Assert.Equal(
            ["packageSources", "packageSourceMapping", "config"],
            configuration.Elements().Select(element => element.Name.LocalName).ToArray());

        var packageSources = Assert.Single(configuration.Elements("packageSources"));
        Assert.Equal("channel", Assert.Single(packageSources.Elements("add")).Attribute("key")?.Value);

        var packageSourceMapping = Assert.Single(configuration.Elements("packageSourceMapping"));
        var sourceMapping = Assert.Single(packageSourceMapping.Elements("packageSource"));
        Assert.Equal(
            ["Contoso.*", "Aspire*"],
            sourceMapping.Elements("package").Select(element => element.Attribute("pattern")!.Value).ToArray());

        var globalPackagesFolder = Assert.Single(Assert.Single(configuration.Elements("config")).Elements("add"));
        Assert.Equal(
            Path.Combine(workspace.WorkspaceRoot.FullName, "unused"),
            globalPackagesFolder.Attribute("value")?.Value);
    }

    [Fact]
    public async Task CreateComposedAsync_MergesSourceNamesCaseInsensitively()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var userConfigPath = Path.Combine(workspace.CreateDirectory("user").FullName, "NuGet.Config");
        await File.WriteAllTextAsync(userConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://inherited.example.com/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="inherited-user" />
                  <add key="ClearTextPassword" value="inherited-password" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);
        var workspaceConfigPath = Path.Combine(workspace.CreateDirectory("repo").FullName, "NuGet.Config");
        await File.WriteAllTextAsync(workspaceConfigPath, """
            <configuration>
              <packageSources>
                <add key="PRIVATE" value="https://replacement.example.com/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <PRIVATE>
                  <add key="Username" value="replacement-user" />
                  <add key="ClearTextPassword" value="replacement-password" />
                </PRIVATE>
              </packageSourceCredentials>
            </configuration>
            """);

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [workspaceConfigPath, userConfigPath],
            []);

        var document = XDocument.Load(config.ConfigFile.FullName);
        var packageSource = document.Descendants("packageSources").Elements("add").Single();
        Assert.Equal("PRIVATE", packageSource.Attribute("key")?.Value);
        Assert.Equal("https://replacement.example.com/v3/index.json", packageSource.Attribute("value")?.Value);

        var credentials = document.Descendants("packageSourceCredentials").Elements().Single();
        Assert.Equal("PRIVATE", credentials.Name.LocalName);
        Assert.Equal(
            [("Username", "replacement-user"), ("ClearTextPassword", "replacement-password")],
            credentials.Elements("add")
                .Select(element => (element.Attribute("key")!.Value, element.Attribute("value")!.Value))
                .ToArray());
    }

    [Fact]
    public async Task CreateComposedAsync_DetectsCredentialBearingConfigValue()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(configPath, """
            <configuration>
              <config>
                <add key="http_proxy" value="https://user:password@example.invalid" />
              </config>
            </configuration>
            """);

        using var config = await TemporaryNuGetConfig.CreateComposedAsync([configPath], []);

        Assert.True(config.ContainsCredentialMaterial);
    }

    [Fact]
    public async Task CreateComposedAsync_MergesTrustedRepositoriesByServiceIndex()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(_outputHelper);
        var userConfigPath = Path.Combine(workspace.CreateDirectory("user").FullName, "NuGet.Config");
        await File.WriteAllTextAsync(userConfigPath, """
            <configuration>
              <trustedSigners>
                <repository name="inherited" serviceIndex="https://packages.example.com/v3/index.json">
                  <certificate fingerprint="INHERITED" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
                </repository>
                <repository name="case-variant" serviceIndex="https://PACKAGES.example.com/v3/index.json">
                  <certificate fingerprint="CASE-VARIANT" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
                </repository>
              </trustedSigners>
            </configuration>
            """);
        var workspaceConfigPath = Path.Combine(workspace.CreateDirectory("repo").FullName, "NuGet.Config");
        await File.WriteAllTextAsync(workspaceConfigPath, """
            <configuration>
              <trustedSigners>
                <repository name="renamed" serviceIndex="https://packages.example.com/v3/index.json">
                  <certificate fingerprint="REPLACEMENT" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
                </repository>
              </trustedSigners>
            </configuration>
            """);

        using var config = await TemporaryNuGetConfig.CreateComposedAsync(
            [workspaceConfigPath, userConfigPath],
            []);

        var repositories = XDocument.Load(config.ConfigFile.FullName)
            .Descendants("trustedSigners")
            .Elements("repository")
            .ToArray();
        Assert.Collection(
            repositories,
            repository =>
            {
                Assert.Equal("renamed", repository.Attribute("name")?.Value);
                Assert.Equal("REPLACEMENT", repository.Element("certificate")?.Attribute("fingerprint")?.Value);
            },
            repository => Assert.Equal("case-variant", repository.Attribute("name")?.Value));
    }
}
