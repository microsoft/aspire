// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Backchannel;

public class AuxiliaryBackchannelMonitorTests
{
    [Fact]
    public void IsAppHostInScopeOfDirectory_WithSymlinkedPaths_IsInScope()
    {
        // The OS reports a process's current directory physically (for example macOS temp dirs under
        // /var -> /private/var), while a file-based AppHost reports its path unresolved. The in-scope check
        // must resolve symlinks on both operands or it treats an in-scope AppHost as out of scope, which made
        // CWD-based 'aspire describe' report "No running AppHost found". See https://github.com/microsoft/aspire/issues/17618.
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-symlink-");
        try
        {
            var realDirectory = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "real"));
            var symlinkDirectory = Path.Combine(tempRoot.FullName, "link");
            Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);

            // AppHost reported through the real directory, working directory reached through the symlink.
            var appHostPathViaReal = Path.Combine(realDirectory.FullName, "apphost.cs");
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(appHostPathViaReal, symlinkDirectory));

            // And the reverse: AppHost reached through the symlink, working directory the real path.
            var appHostPathViaSymlink = Path.Combine(symlinkDirectory, "apphost.cs");
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(appHostPathViaSymlink, realDirectory.FullName));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_AppHostOutsideWorkingDirectory_IsNotInScope()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-");
        try
        {
            var workingDirectory = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "wd")).FullName;
            var outsideAppHost = Path.Combine(tempRoot.FullName, "other", "apphost.cs");

            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(outsideAppHost, workingDirectory));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_NullOrEmptyAppHostPath_IsNotInScope()
    {
        Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(null, Path.GetTempPath()));
        Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(string.Empty, Path.GetTempPath()));
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_NestedLinkedWorktree_IsNotInScopeOfPrimary()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-worktree-");
        try
        {
            var primaryRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
            var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
            TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));

            var primaryAppHost = Path.Combine(primaryRoot, "AppHost.csproj");
            var nestedAppHost = Path.Combine(worktreeRoot, "AppHost.csproj");

            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(primaryAppHost, primaryRoot));
            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(nestedAppHost, primaryRoot));
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(nestedAppHost, worktreeRoot));
            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(primaryAppHost, worktreeRoot));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_Submodule_IsInScopeOfPrimary()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-submodule-");
        try
        {
            var primaryRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
            var submoduleRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, "extern", "dep")).FullName;
            TestGitWorktree.WriteGitDirFile(
                submoduleRoot,
                Path.Combine(primaryRoot, ".git", "modules", "dep"));

            var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");
            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(submoduleAppHost, primaryRoot));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsAppHostInScopeOfDirectory_SubmoduleInsideLinkedWorktree_UsesEnclosingWorktree()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-scope-linked-submodule-");
        try
        {
            var primaryRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(primaryRoot, ".git"));
            var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryRoot, ".worktrees", "feature")).FullName;
            var adminDirectory = TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, Path.Combine(primaryRoot, ".git"));
            var submoduleRoot = Directory.CreateDirectory(Path.Combine(worktreeRoot, "extern", "dep")).FullName;
            TestGitWorktree.WriteGitDirFile(submoduleRoot, Path.Combine(adminDirectory, "modules", "dep"));
            var submoduleAppHost = Path.Combine(submoduleRoot, "AppHost.csproj");

            Assert.True(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(submoduleAppHost, worktreeRoot));
            Assert.False(AuxiliaryBackchannelMonitor.IsAppHostInScopeOfDirectory(submoduleAppHost, primaryRoot));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
