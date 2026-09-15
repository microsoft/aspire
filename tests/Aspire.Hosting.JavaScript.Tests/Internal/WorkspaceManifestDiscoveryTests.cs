// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class WorkspaceManifestDiscoveryTests
{
    [Fact]
    public void DiscoverFlagsPackageJsonAndLockfilePresence()
    {
        using var root = new TempRoot();
        root.CreateFile("package.json");
        root.CreateFile("pnpm-lock.yaml");

        var result = WorkspaceManifestDiscovery.Discover(root.Path);

        Assert.True(result.HasPackageJson);
        Assert.True(result.HasLockfile);
    }

    [Fact]
    public void DiscoverReportsMissingPackageJsonAndLockfile()
    {
        using var root = new TempRoot();

        var result = WorkspaceManifestDiscovery.Discover(root.Path);

        Assert.False(result.HasPackageJson);
        Assert.False(result.HasLockfile);
        Assert.Empty(result.RootFiles);
        Assert.Empty(result.RootDirs);
    }

    [Fact]
    public void DiscoverOrdersRootFilesPackageJsonThenLockfilesByPrecedence()
    {
        using var root = new TempRoot();
        root.CreateFile("package.json");
        // Multiple lockfiles are emitted in package-manager precedence order, not
        // filesystem order: npm, yarn, pnpm, bun.
        root.CreateFile("yarn.lock");
        root.CreateFile("package-lock.json");

        var result = WorkspaceManifestDiscovery.Discover(root.Path);

        Assert.Equal(["package.json", "package-lock.json", "yarn.lock"], result.RootFiles);
    }

    [Fact]
    public void DiscoverIncludesOptionalConfigFilesWhenPresent()
    {
        using var root = new TempRoot();
        root.CreateFile("package.json");
        root.CreateFile("pnpm-lock.yaml");
        root.CreateFile("pnpm-workspace.yaml");
        root.CreateFile(".npmrc");

        var result = WorkspaceManifestDiscovery.Discover(root.Path);

        Assert.Equal(["package.json", "pnpm-lock.yaml", "pnpm-workspace.yaml", ".npmrc"], result.RootFiles);
    }

    [Fact]
    public void DiscoverIncludesYarnDirWhenPresent()
    {
        using var root = new TempRoot();
        root.CreateFile("package.json");
        root.CreateFile(".yarnrc.yml");
        root.CreateDir(".yarn");

        var result = WorkspaceManifestDiscovery.Discover(root.Path);

        Assert.Equal([".yarn"], result.RootDirs);
        Assert.Contains(".yarnrc.yml", result.RootFiles);
    }

    [Fact]
    public void DiscoverIgnoresUnrelatedRootFiles()
    {
        using var root = new TempRoot();
        root.CreateFile("package.json");
        root.CreateFile("README.md");
        root.CreateFile("tsconfig.json");

        var result = WorkspaceManifestDiscovery.Discover(root.Path);

        Assert.Equal(["package.json"], result.RootFiles);
        Assert.Empty(result.RootDirs);
    }

    [Fact]
    public void RecognizedLockfileNamesAreOrderedByPrecedence()
    {
        Assert.Equal(
            ["package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb"],
            WorkspaceManifestDiscovery.RecognizedLockfileNames);
    }

    private sealed class TempRoot : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("aspire-ws-discovery-");

        public string Path => _dir.FullName;

        public void CreateFile(string name) =>
            File.WriteAllText(System.IO.Path.Combine(_dir.FullName, name), string.Empty);

        public void CreateDir(string name) =>
            Directory.CreateDirectory(System.IO.Path.Combine(_dir.FullName, name));

        public void Dispose() => _dir.Delete(recursive: true);
    }
}
