// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Cli.Tests.Packaging;

/// <summary>
/// Asserts that <c>Aspire.Cli.csproj</c> is wired so that the <c>.aspire-install.json</c>
/// sidecar is copied into <c>$(PublishDir)</c> only for RID-specific tool nupkgs (not the
/// pointer nupkg) by inspecting the csproj XML. Running <c>./build.sh --pack</c> from a
/// unit test is too heavy, so this test verifies the wiring deterministically.
/// </summary>
public class ToolNupkgPackagingTests
{
    [Fact]
    public void Csproj_DefinesIsRidSpecificToolPackageGate()
    {
        var doc = LoadCliCsproj();

        var gateGroup = doc.Root!
            .Elements("PropertyGroup")
            .FirstOrDefault(pg => pg.Element("IsRidSpecificToolPackage") is not null);

        Assert.NotNull(gateGroup);

        var condition = gateGroup!.Attribute("Condition")?.Value ?? string.Empty;
        Assert.Contains("'$(IsCliToolProject)' == 'true'", condition);
        Assert.Contains("'$(RuntimeIdentifier)' != ''", condition);

        Assert.Equal("true", gateGroup.Element("IsRidSpecificToolPackage")!.Value.Trim());
    }

    [Fact]
    public void Csproj_PreparePreBuiltCliBinaryForPackTool_CopiesSidecarGatedOnRidSpecific()
    {
        var doc = LoadCliCsproj();

        var target = doc.Root!
            .Elements("Target")
            .FirstOrDefault(t => (string?)t.Attribute("Name") == "_PreparePreBuiltCliBinaryForPackTool");

        Assert.NotNull(target);
        Assert.Equal("PackTool", (string?)target!.Attribute("BeforeTargets"));

        var sidecarCopy = target.Elements("Copy")
            .FirstOrDefault(c => ((string?)c.Attribute("SourceFiles") ?? string.Empty).Contains(".aspire-install.json"));

        Assert.NotNull(sidecarCopy);

        var condition = sidecarCopy!.Attribute("Condition")?.Value ?? string.Empty;
        Assert.Contains("'$(IsRidSpecificToolPackage)' == 'true'", condition);

        var destination = sidecarCopy.Attribute("DestinationFolder")?.Value ?? string.Empty;
        Assert.Contains("$(PublishDir)", destination);

        var source = sidecarCopy.Attribute("SourceFiles")!.Value;
        Assert.Contains("$(MSBuildThisFileDirectory)", source);
        Assert.EndsWith(".aspire-install.json", source);
    }

    private static XDocument LoadCliCsproj()
    {
        var csprojPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Aspire.Cli.csproj");
        Assert.True(File.Exists(csprojPath), $"Expected csproj at {csprojPath}");
        return XDocument.Load(csprojPath);
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
