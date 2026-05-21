// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.Packaging;

public class TemporaryNuGetConfigTests
{
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
}
