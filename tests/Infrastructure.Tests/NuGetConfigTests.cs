// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

using Xunit;

namespace Infrastructure.Tests;

public sealed class NuGetConfigTests
{
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

        Assert.All(root.Element("packageSources")!.Elements("add"), source =>
        {
            var uri = new Uri(source.Attribute("value")!.Value);
            Assert.Contains(uri.Host, new[] { "pkgs.dev.azure.com", "dnceng.pkgs.visualstudio.com" });
        });
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
}
