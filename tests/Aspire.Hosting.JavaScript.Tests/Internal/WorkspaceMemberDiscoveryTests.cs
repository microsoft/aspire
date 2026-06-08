// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class WorkspaceMemberDiscoveryTests
{
    [Fact]
    public void DiscoverReadsPnpmWorkspaceYamlMembers()
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "my-monorepo" }""");
        root.WriteFile("pnpm-workspace.yaml", "packages:\n  - \"packages/*\"\n");
        root.WriteFile("pnpm-lock.yaml", "");
        root.WriteMember("packages/web", "@my/web");
        root.WriteMember("packages/api", "@my/api");

        var info = WorkspaceMemberDiscovery.Discover(root.Path, "pnpm");

        Assert.Equal(["packages/api", "packages/web"], info.WorkspaceDirs);
        Assert.Equal("my-monorepo", info.AppName);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("yarn")]
    [InlineData("bun")]
    public void DiscoverReadsRootPackageJsonWorkspacesForNonPnpm(string packageManager)
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "root-app", "workspaces": ["packages/*", "apps/web"] }""");
        root.WriteMember("packages/utils", "@root/utils");
        root.WriteMember("apps/web", "web");

        var info = WorkspaceMemberDiscovery.Discover(root.Path, packageManager);

        Assert.Equal(["apps/web", "packages/utils"], info.WorkspaceDirs);
        Assert.Equal("root-app", info.AppName);
    }

    [Fact]
    public void DiscoverExposesDirToPackageNamePairs()
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "root", "workspaces": ["packages/*"] }""");
        root.WriteMember("packages/web", "@scope/web");
        root.WriteMember("packages/api", "@scope/api");

        var info = WorkspaceMemberDiscovery.Discover(root.Path, "npm");

        // The package-manager filter (--filter / --workspace=) selects by the package NAME,
        // while the expander resolves DIRECTORIES, so the discovery must surface the mapping.
        Assert.Collection(info.Members,
            m => { Assert.Equal("packages/api", m.RelativeDir); Assert.Equal("@scope/api", m.PackageName); },
            m => { Assert.Equal("packages/web", m.RelativeDir); Assert.Equal("@scope/web", m.PackageName); });
    }

    [Fact]
    public void DiscoverUsesDirectoryNameWhenMemberPackageJsonHasNoName()
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "root", "workspaces": ["packages/*"] }""");
        // A member package.json without a "name" field falls back to the directory name.
        root.WriteFile("packages/legacy/package.json", "{}");

        var info = WorkspaceMemberDiscovery.Discover(root.Path, "npm");

        var member = Assert.Single(info.Members);
        Assert.Equal("packages/legacy", member.RelativeDir);
        Assert.Equal("legacy", member.PackageName);
    }

    [Fact]
    public void DiscoverFoldsInRootManifestFilesAndDirs()
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "root", "workspaces": ["packages/*"] }""");
        root.WriteFile("yarn.lock", "");
        root.WriteFile(".yarnrc.yml", "");
        root.CreateDir(".yarn");
        root.WriteMember("packages/web", "web");

        var info = WorkspaceMemberDiscovery.Discover(root.Path, "yarn");

        Assert.Equal(["package.json", "yarn.lock", ".yarnrc.yml"], info.RootFiles);
        Assert.Equal([".yarn"], info.RootDirs);
    }

    [Fact]
    public void DiscoverThrowsForUnsupportedPattern()
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "root", "workspaces": ["packages/**"] }""");

        Assert.Throws<DistributedApplicationException>(() => WorkspaceMemberDiscovery.Discover(root.Path, "npm"));
    }

    [Fact]
    public void DiscoverReturnsEmptyMembersWhenNoWorkspacesDeclared()
    {
        using var root = new TempRoot();
        root.WriteFile("package.json", """{ "name": "solo" }""");

        var info = WorkspaceMemberDiscovery.Discover(root.Path, "npm");

        Assert.Empty(info.WorkspaceDirs);
        Assert.Empty(info.Members);
        Assert.Equal("solo", info.AppName);
    }

    private sealed class TempRoot : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("aspire-ws-member-");

        public string Path => _dir.FullName;

        public void WriteFile(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(_dir.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void CreateDir(string relativePath) =>
            Directory.CreateDirectory(System.IO.Path.Combine(_dir.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        public void WriteMember(string relativeDir, string packageName) =>
            WriteFile($"{relativeDir}/package.json", $$"""{ "name": "{{packageName}}" }""");

        public void Dispose() => _dir.Delete(recursive: true);
    }
}
