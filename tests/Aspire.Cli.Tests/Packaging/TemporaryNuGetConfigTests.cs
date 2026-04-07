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
}
