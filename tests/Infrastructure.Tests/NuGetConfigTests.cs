// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

using Xunit;

namespace Infrastructure.Tests;

public sealed class NuGetConfigTests
{
    [Fact]
    public void TemplateRestoresUseOnlyLocalAndApprovedFeeds()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot.Path, "tests", "Shared", "TemplatesTesting", "data", "nuget8.config"));
        var root = document.Root;
        Assert.NotNull(root);
        var sources = root.Element("packageSources")!.Elements("add")
            .ToDictionary(element => element.Attribute("key")!.Value, element => element.Attribute("value")!.Value, StringComparer.Ordinal);
        string[] expectedSources = ["built-local", "dotnet-eng", "dotnet-public", "dotnet10", "dotnet9"];
        Assert.Equal(expectedSources, sources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("%BUILT_NUGETS_PATH%", sources["built-local"]);
        Assert.All(sources.Where(source => source.Key != "built-local"), source => AssertApprovedSource(source.Value));

        var mappings = root.Element("packageSourceMapping")!.Elements("packageSource")
            .ToDictionary(element => element.Attribute("key")!.Value,
                element => element.Elements("package").Select(package => package.Attribute("pattern")!.Value).ToArray(),
                StringComparer.Ordinal);
        Assert.Equal(expectedSources, mappings.Keys.Order(StringComparer.Ordinal));
        Assert.All(mappings.Values, patterns => Assert.Equal(["*"], patterns));
    }

    [Fact]
    public void AuditSourceIsSeparateFromPackageSources()
    {
        var root = XDocument.Load(Path.Combine(RepoRoot.Path, "NuGet.config")).Root!;
        var auditSources = Assert.Single(root.Elements("auditSources"));

        Assert.Collection(auditSources.Elements(),
            clear =>
            {
                Assert.Equal("clear", clear.Name.LocalName);
                Assert.Empty(clear.Attributes());
                Assert.Empty(clear.Nodes());
            },
            source =>
            {
                Assert.Equal("add", source.Name.LocalName);
                Assert.Equal("nuget.org", (string?)source.Attribute("key"));
                Assert.Equal("https://data.nuget.org/v3/index.json", (string?)source.Attribute("value"));
                Assert.Equal(2, source.Attributes().Count());
                Assert.Empty(source.Nodes());
            });

        var sources = root.Element("packageSources")!.Elements("add").ToArray();
        Assert.All(sources.Where(source => (string?)source.Attribute("key") != "nuget-hex1b"),
            source => AssertApprovedSource(source.Attribute("value")!.Value));
        Assert.Equal(
            sources.Select(source => source.Attribute("key")!.Value).Order(StringComparer.Ordinal),
            root.Element("packageSourceMapping")!.Elements("packageSource")
                .Select(source => source.Attribute("key")!.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NuGetOrgIsLimitedToHex1bPackages()
    {
        var root = XDocument.Load(Path.Combine(RepoRoot.Path, "NuGet.config")).Root!;
        var source = Assert.Single(root.Element("packageSources")!.Elements("add"),
            source => (string?)source.Attribute("key") == "nuget-hex1b");
        Assert.Equal("https://api.nuget.org/v3/index.json", (string?)source.Attribute("value"));

        var mapping = Assert.Single(root.Element("packageSourceMapping")!.Elements("packageSource"),
            mapping => (string?)mapping.Attribute("key") == "nuget-hex1b");
        Assert.Equal(["Hex1b", "Hex1b.McpServer", "Hex1b.Tool"],
            mapping.Elements("package").Select(package => (string?)package.Attribute("pattern")));
    }

    [Fact]
    public void DiagnosticsPackagesAreMappedToPublicAndToolsFeeds()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot.Path, "NuGet.config"));
        var root = document.Root;
        Assert.NotNull(root);
        string[] diagnosticsPackages =
        [
            "Microsoft.Diagnostics.NETCore.Client",
            "Microsoft.Diagnostics.Runtime",
        ];

        var source = Assert.Single(
            root.Element("packageSources")!.Elements("add"),
            element => (string?)element.Attribute("key") == "dotnet-tools");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json",
            (string?)source.Attribute("value"));

        var mapping = Assert.Single(
            root.Element("packageSourceMapping")!.Elements("packageSource"),
            element => (string?)element.Attribute("key") == "dotnet-tools");
        var patterns = mapping.Elements("package")
            .Select(element => element.Attribute("pattern")!.Value)
            .ToArray();

        Assert.Equal(diagnosticsPackages, patterns);

        var publicMapping = Assert.Single(
            root.Element("packageSourceMapping")!.Elements("packageSource"),
            element => (string?)element.Attribute("key") == "dotnet-public");
        var publicPatterns = publicMapping.Elements("package")
            .Select(element => element.Attribute("pattern")!.Value);

        foreach (var package in diagnosticsPackages)
        {
            Assert.Contains(package, publicPatterns);
        }
    }

    private static void AssertApprovedSource(string source)
    {
        Assert.True(
            source.StartsWith("https://pkgs.dev.azure.com/dnceng/", StringComparison.Ordinal) ||
            source.StartsWith("https://dnceng.pkgs.visualstudio.com/public/", StringComparison.Ordinal),
            $"Unexpected package source: {source}");
    }
}
