// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;
using Aspire.Cli.Packaging;

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
        var allPackagesMapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='https://example.com/feed2']/package[@pattern='*']");
        Assert.NotNull(allPackagesMapping);

        // Verify other specific mappings are also included
        var aspireMapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='https://example.com/feed1']/package[@pattern='Aspire.*']");
        Assert.NotNull(aspireMapping);

        var microsoftMapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='https://example.com/feed1']/package[@pattern='Microsoft.*']");
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
        var feed1Mapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='https://feed1.example.com']/package[@pattern='*']");
        Assert.NotNull(feed1Mapping);

        var feed2Mapping = xmlDoc.SelectSingleNode("//packageSourceMapping/packageSource[@key='https://feed2.example.com']/package[@pattern='*']");
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
            Assert.Equal(add.Attributes!["key"]!.Value, add.Attributes!["value"]!.Value);
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

        // The mapping for our source must be present and exactly equal the input source.
        Assert.Contains(source, mappingKeys);
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
}
