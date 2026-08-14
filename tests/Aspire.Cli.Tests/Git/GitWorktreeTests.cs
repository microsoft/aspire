// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;
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
    public void TryGetLinkedWorktreeRoot_LinkedWorktree_ReturnsWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var worktreeRoot = workspace.WorkspaceRoot.FullName;
        WriteGitDirFile(worktreeRoot, $"gitdir: {Path.Combine(worktreeRoot, ".git", "worktrees", "feature")}");
        var appHostPath = Path.Combine(worktreeRoot, "AppHost", "AppHost.csproj");

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(appHostPath);

        Assert.NotNull(linkedRoot);
        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_RelativeGitDir_ReturnsWorktreeRoot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var worktreeRoot = workspace.WorkspaceRoot.FullName;
        WriteGitDirFile(worktreeRoot, "gitdir: ../.git/worktrees/feature\n");

        var linkedRoot = GitWorktree.TryGetLinkedWorktreeRoot(worktreeRoot);

        Assert.NotNull(linkedRoot);
        Assert.Equal(PathNormalizer.ResolveSymlinks(worktreeRoot), linkedRoot);
    }

    [Fact]
    public void TryGetLinkedWorktreeRoot_SubmoduleGitFile_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".git"));
        var submoduleRoot = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "extern", "dep")).FullName;
        WriteGitDirFile(submoduleRoot, $"gitdir: {Path.Combine(workspace.WorkspaceRoot.FullName, ".git", "modules", "dep")}");
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
        WriteGitDirFile(worktreeRoot, $"gitdir: {Path.Combine(primaryRoot, ".git", "worktrees", "feature")}");
        var nestedAppHost = Path.Combine(worktreeRoot, "AppHost.csproj");
        var primaryAppHost = Path.Combine(primaryRoot, "AppHost.csproj");

        Assert.False(GitWorktree.IsSameWorktreeScope(nestedAppHost, primaryRoot));
        Assert.True(GitWorktree.IsSameWorktreeScope(primaryAppHost, primaryRoot));
        Assert.True(GitWorktree.IsSameWorktreeScope(nestedAppHost, worktreeRoot));
        Assert.False(GitWorktree.IsSameWorktreeScope(primaryAppHost, worktreeRoot));
    }

    [Fact]
    public void IsSameWorktreeScope_Submodule_RemainsInScopeOfPrimary()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var primaryRoot = workspace.WorkspaceRoot.FullName;
        Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
        var submoduleRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, "extern", "dep")).FullName;
        WriteGitDirFile(submoduleRoot, $"gitdir: {Path.Combine(primaryRoot, ".git", "modules", "dep")}");
        var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");

        Assert.True(GitWorktree.IsSameWorktreeScope(submoduleAppHost, primaryRoot));
    }

    private static void WriteGitDirFile(string worktreeRoot, string contents)
    {
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"), contents);
    }
}
