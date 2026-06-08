// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class WorkspacePatternExpanderTests
{
    [Fact]
    public void TrailingStarResolvesOnlyChildDirsWithPackageJson()
    {
        using var root = new TempWorkspace();
        root.CreatePackage("packages/web");
        root.CreatePackage("packages/api");
        // A child directory without a package.json must not be treated as a member.
        root.CreateDir("packages/docs");
        root.CreateFile("packages/docs/README.md");

        var members = WorkspacePatternExpander.Expand(root.Path, ["packages/*"]);

        Assert.Equal(["packages/api", "packages/web"], members);
    }

    [Fact]
    public void TrailingStarResultsAreSortedOrdinally()
    {
        using var root = new TempWorkspace();
        root.CreatePackage("packages/zeta");
        root.CreatePackage("packages/Alpha");
        root.CreatePackage("packages/beta");

        var members = WorkspacePatternExpander.Expand(root.Path, ["packages/*"]);

        // Ordinal ordering: uppercase letters sort before lowercase.
        Assert.Equal(["packages/Alpha", "packages/beta", "packages/zeta"], members);
    }

    [Fact]
    public void TrailingStarSkipsDotDirectories()
    {
        using var root = new TempWorkspace();
        root.CreatePackage("packages/web");
        // Hidden tooling directories (e.g. .turbo) carry a package.json but are not members.
        root.CreatePackage("packages/.cache");

        var members = WorkspacePatternExpander.Expand(root.Path, ["packages/*"]);

        Assert.Equal(["packages/web"], members);
    }

    [Fact]
    public void LiteralPathWithPackageJsonIncluded()
    {
        using var root = new TempWorkspace();
        root.CreatePackage("apps/web");

        var members = WorkspacePatternExpander.Expand(root.Path, ["apps/web"]);

        Assert.Equal(["apps/web"], members);
    }

    [Fact]
    public void LiteralPathWithoutPackageJsonExcluded()
    {
        using var root = new TempWorkspace();
        root.CreateDir("apps/web");

        var members = WorkspacePatternExpander.Expand(root.Path, ["apps/web"]);

        Assert.Empty(members);
    }

    [Fact]
    public void MissingParentDirectoryYieldsNoMembers()
    {
        using var root = new TempWorkspace();

        var members = WorkspacePatternExpander.Expand(root.Path, ["packages/*"]);

        Assert.Empty(members);
    }

    [Fact]
    public void NegatedAndEmptyPatternsAreSkipped()
    {
        using var root = new TempWorkspace();
        root.CreatePackage("packages/web");

        var members = WorkspacePatternExpander.Expand(root.Path, ["!apps/legacy", "", "  ", "packages/*"]);

        Assert.Equal(["packages/web"], members);
    }

    [Fact]
    public void DuplicatePatternsResolveToDistinctSortedSet()
    {
        using var root = new TempWorkspace();
        root.CreatePackage("apps/web");

        var members = WorkspacePatternExpander.Expand(root.Path, ["apps/web", "./apps/web", "apps/web/"]);

        Assert.Equal(["apps/web"], members);
    }

    private sealed class TempWorkspace : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("aspire-ws-expander-");

        public string Path => _dir.FullName;

        public void CreateDir(string relative) =>
            Directory.CreateDirectory(System.IO.Path.Combine(_dir.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        public void CreateFile(string relative)
        {
            var full = System.IO.Path.Combine(_dir.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, string.Empty);
        }

        public void CreatePackage(string relativeDir) =>
            CreateFile($"{relativeDir}/package.json");

        public void Dispose() => _dir.Delete(recursive: true);
    }
}
