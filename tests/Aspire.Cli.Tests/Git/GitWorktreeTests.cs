// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Tests.Git;

public class GitWorktreeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void TryGetLinkedWorktreeRoot_PrimaryCheckout_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".git"));
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost", "AppHost.csproj");

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(appHostPath));
        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_StandardCommonGitDirectory_ReturnsWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(fixtureRoot, "primary", ".git"));
        var appHostPath = Path.Combine(worktreeRoot, "AppHost", "AppHost.csproj");

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(appHostPath);

        Assert.NotNull(linkedRoot);
        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Theory]
    [InlineData("repo.git")]
    [InlineData("separate-git")]
    public void TryGetLinkedWorktreeRoot_NonDotGitCommonDirectory_ReturnsWorktreeRoot(string commonGitDirectoryName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(fixtureRoot, commonGitDirectoryName));

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);

        Assert.NotNull(linkedRoot);
        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_RelativeGitDir_ReturnsWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var worktreeRoot = workspace.WorkspaceRoot.FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(worktreeRoot, "primary", ".git"),
            useRelativePaths: true);

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);

        Assert.NotNull(linkedRoot);
        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_CheckoutAliasWithRelativeMetadata_ReturnsCanonicalWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(
            Path.Combine(fixtureRoot, "physical", "checkouts", "feature")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(fixtureRoot, "physical", "primary", ".git"),
            useRelativePaths: true);
        var aliasRoot = Path.Combine(fixtureRoot, "aliases", "feature");
        Directory.CreateDirectory(Path.GetDirectoryName(aliasRoot)!);

        try
        {
            try
            {
                ReparsePoint.CreateOrReplace(aliasRoot, worktreeRoot);
            }
            catch (UnauthorizedAccessException ex)
            {
                Assert.Skip($"Cannot create a directory symlink or junction in this environment: {ex.Message}");
            }
            catch (IOException ex)
            {
                Assert.Skip($"Directory symlink or junction creation failed in this environment: {ex.Message}");
            }

            var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(aliasRoot);

            Assert.NotNull(linkedRoot);
            Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
        }
        finally
        {
            ReparsePoint.RemoveIfExists(aliasRoot);
        }
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_CaseVariantCheckoutPath_ReturnsFilesystemCanonicalCasing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(fixtureRoot, "primary", ".git"));
        var caseVariantRoot = Path.Combine(fixtureRoot, "WORKTREE");
        if (!Directory.Exists(caseVariantRoot))
        {
            Assert.Skip("The test requires a case-insensitive filesystem.");
        }

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(caseVariantRoot);

        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_AdminAliasWithRelativeBackPointer_ReturnsCanonicalWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "checkout")).FullName;
        var adminDirectory = Directory.CreateDirectory(
            Path.Combine(fixtureRoot, "physical", "primary", ".git", "worktrees", "feature")).FullName;
        var aliasDirectory = Path.Combine(fixtureRoot, "aliases", "admin");
        Directory.CreateDirectory(Path.GetDirectoryName(aliasDirectory)!);

        try
        {
            try
            {
                ReparsePoint.CreateOrReplace(aliasDirectory, adminDirectory);
            }
            catch (UnauthorizedAccessException ex)
            {
                Assert.Skip($"Cannot create a directory symlink or junction in this environment: {ex.Message}");
            }
            catch (IOException ex)
            {
                Assert.Skip($"Directory symlink or junction creation failed in this environment: {ex.Message}");
            }

            var gitFilePath = TestGitWorktree.WriteGitDirFile(worktreeRoot, aliasDirectory);
            File.WriteAllText(
                Path.Combine(adminDirectory, "gitdir"),
                Path.GetRelativePath(adminDirectory, gitFilePath) + Environment.NewLine);

            var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);

            Assert.NotNull(linkedRoot);
            Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
        }
        finally
        {
            ReparsePoint.RemoveIfExists(aliasDirectory);
        }
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_GitDirWithTrailingDirectorySeparator_ReturnsWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var worktreeRoot = workspace.WorkspaceRoot.FullName;
        var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(worktreeRoot, "primary", ".git"));
        File.WriteAllText(
            Path.Combine(worktreeRoot, ".git"),
            $"gitdir: {adminDirectory}{Path.DirectorySeparatorChar}{Environment.NewLine}");

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);

        Assert.NotNull(linkedRoot);
        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_UppercaseWorktreesAdminDirectory_UsesPlatformCasing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        var adminDirectory = Path.Combine(fixtureRoot, "primary", ".git", "WORKTREES", "feature");
        var gitFilePath = TestGitWorktree.WriteGitDirFile(worktreeRoot, adminDirectory);
        File.WriteAllText(
            Path.Combine(adminDirectory, "gitdir"),
            gitFilePath + Environment.NewLine);

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);
        var expectedRoot = OperatingSystem.IsWindows()
            ? PathNormalizer.ResolveSymlinks(worktreeRoot)
            : null;

        Assert.Equal(expectedRoot, linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_CaseVariantBackPointer_UsesFilesystemIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var worktreeRoot = workspace.WorkspaceRoot.FullName;
        var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(worktreeRoot, "primary", ".git"));
        var caseVariantGitFile = Path.Combine(worktreeRoot, ".GIT");
        File.WriteAllText(
            Path.Combine(adminDirectory, "gitdir"),
            caseVariantGitFile + Environment.NewLine);

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);
        var expectedRoot = File.Exists(caseVariantGitFile)
            ? PathNormalizer.ResolveSymlinks(worktreeRoot)
            : null;

        Assert.Equal(expectedRoot, linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_SymlinkAliasBackPointer_ReturnsWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(fixtureRoot, "primary", ".git"));
        var aliasRoot = Path.Combine(fixtureRoot, "worktree-alias");

        try
        {
            try
            {
                ReparsePoint.CreateOrReplace(aliasRoot, worktreeRoot);
            }
            catch (UnauthorizedAccessException ex)
            {
                Assert.Skip($"Cannot create a directory symlink or junction in this environment: {ex.Message}");
            }
            catch (IOException ex)
            {
                Assert.Skip($"Directory symlink or junction creation failed in this environment: {ex.Message}");
            }

            File.WriteAllText(
                Path.Combine(adminDirectory, "gitdir"),
                Path.Combine(aliasRoot, ".git") + Environment.NewLine);

            var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);

            Assert.NotNull(linkedRoot);
            Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
        }
        finally
        {
            ReparsePoint.RemoveIfExists(aliasRoot);
        }
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_SubmoduleInsideLinkedWorktree_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(
            worktreeRoot,
            Path.Combine(fixtureRoot, "primary", ".git"));
        var submoduleRoot = Directory.CreateDirectory(Path.Combine(worktreeRoot, "extern", "dep")).FullName;
        TestGitWorktree.WriteGitDirFile(
            submoduleRoot,
            Path.Combine(adminDirectory, "modules", "dep"));

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(Path.Combine(submoduleRoot, "AppHost.csproj")));
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_ReciprocalBackPointerOutsideWorktrees_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        var adminDirectory = Path.Combine(fixtureRoot, "primary", ".git", "modules", "dep");
        var gitFilePath = TestGitWorktree.WriteGitDirFile(worktreeRoot, adminDirectory);
        File.WriteAllText(
            Path.Combine(adminDirectory, "gitdir"),
            gitFilePath + Environment.NewLine);

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot));
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_BackPointerToDifferentCheckout_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var commonGitDirectory = Path.Combine(fixtureRoot, "primary", ".git");
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, commonGitDirectory);
        var otherWorktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "other-worktree")).FullName;
        var otherGitFile = TestGitWorktree.WriteGitDirFile(
            otherWorktreeRoot,
            Path.Combine(commonGitDirectory, "worktrees", "other"));
        File.WriteAllText(
            Path.Combine(adminDirectory, "gitdir"),
            otherGitFile + Environment.NewLine);

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot));
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_DecoyWithoutBackPointer_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var fixtureRoot = workspace.WorkspaceRoot.FullName;
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "worktree")).FullName;
        TestGitWorktree.WriteGitDirFile(
            worktreeRoot,
            Path.Combine(fixtureRoot, "primary", ".git", "worktrees", "stale"));

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot));
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_SubmoduleGitFile_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".git"));
        var submoduleRoot = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "extern", "dep")).FullName;
        TestGitWorktree.WriteGitDirFile(
            submoduleRoot,
            Path.Combine(workspace.WorkspaceRoot.FullName, ".git", "modules", "dep"));
        var appHostPath = Path.Combine(submoduleRoot, "AppHost.csproj");

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(appHostPath));
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_NotAGitRepo_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(workspace.WorkspaceRoot.FullName));
        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(null));
        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(""));
    }

    [Fact]
    public void IsSameWorktreeScope_NestedLinkedWorktree_IsOutOfScopeOfPrimary()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = workspace.WorkspaceRoot.FullName;
        Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));
        var nestedAppHost = Path.Combine(worktreeRoot, "AppHost.csproj");
        var primaryAppHost = Path.Combine(primaryRoot, "AppHost.csproj");

        Assert.False(GitWorktree.IsSameWorktreeScope(nestedAppHost, primaryRoot));
        Assert.True(GitWorktree.IsSameWorktreeScope(primaryAppHost, primaryRoot));
        Assert.True(GitWorktree.IsSameWorktreeScope(nestedAppHost, worktreeRoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(primaryAppHost, worktreeRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_SubmoduleInsideLinkedWorktree_UsesEnclosingWorktree()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = workspace.WorkspaceRoot.FullName;
        Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
        var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));
        var submoduleRoot = Directory.CreateDirectory(Path.Combine(worktreeRoot, "extern", "dep")).FullName;
        TestGitWorktree.WriteGitDirFile(submoduleRoot, Path.Combine(adminDirectory, "modules", "dep"));
        var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");

        Assert.Null(GitWorktree.TryGetLinkedWorktreeRoot(submoduleAppHost));
        Assert.True(GitWorktree.IsSameWorktreeScope(submoduleAppHost, worktreeRoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(submoduleAppHost, primaryRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_Submodule_RemainsInScopeOfPrimary()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = workspace.WorkspaceRoot.FullName;
        Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
        var submoduleRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, "extern", "dep")).FullName;
        TestGitWorktree.WriteGitDirFile(
            submoduleRoot,
            Path.Combine(primaryRoot, ".git", "modules", "dep"));
        var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");

        Assert.True(GitWorktree.IsSameWorktreeScope(submoduleAppHost, primaryRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_AppHostNestedInSamePrimaryCheckout_IsSameScope()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoA");
        var appHostPath = Path.Combine(primaryRoot, "sub", "App", "App.csproj");

        Assert.True(GitWorktree.IsSameWorktreeScope(appHostPath, primaryRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_AppHostInDifferentPrimaryCheckout_IsDifferentScope()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repoARoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoA");
        var repoBRoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoB");
        var repoBAppHost = Path.Combine(repoBRoot, "App", "App.csproj");

        Assert.False(GitWorktree.IsSameWorktreeScope(repoBAppHost, repoARoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(Path.Combine(repoARoot, "App", "App.csproj"), repoBRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_SubmodulesInDifferentPrimaryCheckouts_AreDifferentScopes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repoARoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoA");
        var repoBRoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoB");
        var repoBSubmoduleRoot = Directory.CreateDirectory(Path.Combine(repoBRoot, "extern", "dep")).FullName;
        TestGitWorktree.WriteGitDirFile(
            repoBSubmoduleRoot,
            Path.Combine(repoBRoot, ".git", "modules", "dep"));
        var repoBSubmoduleAppHost = Path.Combine(repoBSubmoduleRoot, "AppHost.csproj");

        Assert.True(GitWorktree.IsSameWorktreeScope(repoBSubmoduleAppHost, repoBRoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(repoBSubmoduleAppHost, repoARoot));
    }

    [Fact]
    public void IsSameWorktreeScope_PrimaryCheckoutAndNestedLinkedWorktree_AreDifferentScopes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoA");
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));

        Assert.False(GitWorktree.IsSameWorktreeScope(Path.Combine(worktreeRoot, "App", "App.csproj"), primaryRoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(Path.Combine(primaryRoot, "App", "App.csproj"), worktreeRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_BothOutsideAnyGitRepository_IsSameScope()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var workingDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "plain")).FullName;
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "other", "App.csproj");

        Assert.True(GitWorktree.IsSameWorktreeScope(appHostPath, workingDirectory));
    }

    [Fact]
    public void IsSameWorktreeScope_AppHostOutsideAnyGitRepository_IsDifferentScopeFromCheckout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = CreatePrimaryCheckout(workspace.WorkspaceRoot, "repoA");
        var unmanagedDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "outside")).FullName;
        var unmanagedAppHost = Path.Combine(unmanagedDirectory, "App.csproj");

        Assert.False(GitWorktree.IsSameWorktreeScope(unmanagedAppHost, primaryRoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(Path.Combine(primaryRoot, "App", "App.csproj"), unmanagedDirectory));
    }

    /// <summary>
    /// Creates a primary checkout (a real <c>.git</c> directory) under <paramref name="parent"/> so
    /// scope comparisons are deterministic regardless of whether the test host's temp directory
    /// happens to sit inside a git repository.
    /// </summary>
    private static string CreatePrimaryCheckout(DirectoryInfo parent, string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(parent.FullName, name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        return root;
    }
}
