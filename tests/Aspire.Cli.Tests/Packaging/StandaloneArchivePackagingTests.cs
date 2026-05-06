// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Cli.Tests.Packaging;

/// <summary>
/// Assertions that the standalone CLI archives
/// (<c>aspire-cli-&lt;rid&gt;-&lt;version&gt;.{tar.gz,zip}</c>) ship the route sidecar
/// (<c>.aspire-install.json</c>) at the archive root, with route metadata that
/// matches the consumer for that archive shape.
/// <para>
/// Each archive variant carries the appropriate route and update command:
/// <list type="bullet">
///   <item>WinGet zips (<c>win-*</c>) carry route <c>winget</c> with
///         <c>updateCommand=winget upgrade Microsoft.Aspire</c>.</item>
///   <item>Brew tarballs (<c>osx-*</c>) carry route <c>brew</c> with
///         <c>updateCommand=brew upgrade aspire</c>.</item>
///   <item>Linux tarballs (<c>linux-*</c>) ship NO sidecar — the resolver returns
///         <c>(Unknown, binaryDir)</c> for unmanaged installs.</item>
/// </list>
/// </para>
/// <para>
/// We don't run <c>./build.sh --pack</c> from the unit-test layer (too heavy). Instead
/// we assert the staging logic in <c>eng/clipack/Common.projitems</c> declares the
/// correct sidecar contents per <c>$(CliRuntime)</c>, mirroring the approach
/// <see cref="ToolNupkgPackagingTests"/> takes for the dotnet-tool nupkg sidecar.
/// </para>
/// </summary>
public class StandaloneArchivePackagingTests
{
    private const string SidecarFileName = ".aspire-install.json";

    [Fact]
    public void Projitems_DefinesStageArchiveRouteSidecarTarget()
    {
        var doc = LoadCommonProjitems();
        var target = doc.Root!.Elements("Target")
            .FirstOrDefault(t => (string?)t.Attribute("Name") == "_StageArchiveRouteSidecar");

        Assert.NotNull(target);

        // The target MUST be wired into PublishToDisk's DependsOnTargets — otherwise it
        // would never run during archive staging and the sidecar would be silently absent.
        var publishToDisk = doc.Root!.Elements("Target")
            .FirstOrDefault(t => (string?)t.Attribute("Name") == "PublishToDisk");
        Assert.NotNull(publishToDisk);
        var dependsOn = (string?)publishToDisk!.Attribute("DependsOnTargets") ?? string.Empty;
        Assert.Contains("_StageArchiveRouteSidecar", dependsOn);
    }

    [Fact]
    public void Projitems_StageArchiveRouteSidecar_DeclaresWinGetRouteForWindowsRids()
    {
        var doc = LoadCommonProjitems();
        var target = GetStageArchiveRouteSidecarTarget(doc);

        var routeAssignments = target
            .Descendants("_ArchiveSidecarRoute")
            .Where(e => (string?)e.Parent?.Name?.LocalName == "PropertyGroup")
            .ToList();

        var winRoute = routeAssignments.FirstOrDefault(e =>
            ((string?)e.Attribute("Condition") ?? string.Empty).Contains("'win-'"));

        Assert.NotNull(winRoute);
        Assert.Equal("winget", winRoute!.Value.Trim());
    }

    [Fact]
    public void Projitems_StageArchiveRouteSidecar_DeclaresBrewRouteForMacOsRids()
    {
        var doc = LoadCommonProjitems();
        var target = GetStageArchiveRouteSidecarTarget(doc);

        var routeAssignments = target
            .Descendants("_ArchiveSidecarRoute")
            .Where(e => (string?)e.Parent?.Name?.LocalName == "PropertyGroup")
            .ToList();

        var brewRoute = routeAssignments.FirstOrDefault(e =>
            ((string?)e.Attribute("Condition") ?? string.Empty).Contains("'osx-'"));

        Assert.NotNull(brewRoute);
        Assert.Equal("brew", brewRoute!.Value.Trim());
    }

    [Fact]
    public void Projitems_StageArchiveRouteSidecar_LinuxRidsShipNoSidecar()
    {
        // Linux archives are intentionally unmanaged at the archive layer (no winget /
        // brew consumer). The target is gated on _ArchiveSidecarRoute being non-empty so a
        // linux archive walks the WriteLinesToFile branch only when one of the win/osx
        // conditions has populated the route. Assert that no `linux-` condition writes a
        // sidecar route — and that the target's gate is the `'$(_ArchiveSidecarRoute)' != ''`
        // form so the absence is intentional, not an accidental fallthrough.
        var doc = LoadCommonProjitems();
        var target = GetStageArchiveRouteSidecarTarget(doc);

        var routeAssignments = target
            .Descendants("_ArchiveSidecarRoute")
            .Where(e => (string?)e.Parent?.Name?.LocalName == "PropertyGroup")
            .Select(e => (string?)e.Attribute("Condition") ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(routeAssignments, c => c.Contains("'linux-'"));

        var writeLinesToFile = target.Descendants("WriteLinesToFile").FirstOrDefault();
        Assert.NotNull(writeLinesToFile);
        var condition = (string?)writeLinesToFile!.Attribute("Condition") ?? string.Empty;
        Assert.Contains("'$(_ArchiveSidecarRoute)' != ''", condition);
    }

    [Fact]
    public void Projitems_StageArchiveRouteSidecar_DeclaresWinGetUpdateCommand()
    {
        var doc = LoadCommonProjitems();
        var target = GetStageArchiveRouteSidecarTarget(doc);

        var updateCommands = target
            .Descendants("_ArchiveSidecarUpdateCommand")
            .Where(e => (string?)e.Parent?.Name?.LocalName == "PropertyGroup")
            .ToList();

        var wingetUpdate = updateCommands.FirstOrDefault(e =>
            ((string?)e.Attribute("Condition") ?? string.Empty).Contains("'winget'"));

        Assert.NotNull(wingetUpdate);
        Assert.Equal("winget upgrade Microsoft.Aspire", wingetUpdate!.Value.Trim());
    }

    [Fact]
    public void Projitems_StageArchiveRouteSidecar_DeclaresBrewUpdateCommand()
    {
        var doc = LoadCommonProjitems();
        var target = GetStageArchiveRouteSidecarTarget(doc);

        var updateCommands = target
            .Descendants("_ArchiveSidecarUpdateCommand")
            .Where(e => (string?)e.Parent?.Name?.LocalName == "PropertyGroup")
            .ToList();

        var brewUpdate = updateCommands.FirstOrDefault(e =>
            ((string?)e.Attribute("Condition") ?? string.Empty).Contains("'brew'"));

        Assert.NotNull(brewUpdate);
        Assert.Equal("brew upgrade aspire", brewUpdate!.Value.Trim());
    }

    [Fact]
    public void Projitems_StageArchiveRouteSidecar_StagesSidecarAtArchiveRoot()
    {
        // The Mode B layout requires the sidecar to live next to the binary inside the
        // archive (i.e. at the archive root, since the native binary sits there directly).
        // The path is constructed off $(OutputPath) which Microsoft.DotNet.Build.Tasks.Archives
        // populates with the staging directory it subsequently zips/tars.
        var doc = LoadCommonProjitems();
        var target = GetStageArchiveRouteSidecarTarget(doc);

        var path = target.Descendants("_ArchiveSidecarPath").FirstOrDefault();
        Assert.NotNull(path);
        Assert.Contains("$(OutputPath)", path!.Value);
        Assert.Contains(SidecarFileName, path.Value);

        var content = target.Descendants("_ArchiveSidecarContent").FirstOrDefault();
        Assert.NotNull(content);
        Assert.Contains("$(_ArchiveSidecarRoute)", content!.Value);
        Assert.Contains("$(_ArchiveSidecarUpdateCommand)", content.Value);
    }

    private static XElement GetStageArchiveRouteSidecarTarget(XDocument doc)
    {
        var target = doc.Root!.Elements("Target")
            .FirstOrDefault(t => (string?)t.Attribute("Name") == "_StageArchiveRouteSidecar");
        Assert.NotNull(target);
        return target!;
    }

    private static XDocument LoadCommonProjitems()
    {
        var path = Path.Combine(GetRepoRoot(), "eng", "clipack", "Common.projitems");
        Assert.True(File.Exists(path), $"Expected file at {path}");
        return XDocument.Load(path);
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
