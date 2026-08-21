// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Backchannel;

public class AuxiliaryBackchannelMonitorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void FindConnectionByAppHostPath_WithCaseVariant_FollowsCurrentVolumeBehavior()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("CaseSensitiveAppHost");
        var actualPath = Path.Combine(directory.FullName, "CaseSensitive.AppHost.csproj");
        File.WriteAllText(actualPath, "<Project />");
        var caseVariant = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "casesensitiveapphost",
            "casesensitive.apphost.csproj");
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = actualPath,
                ProcessId = 1
            }
        };

        var result = AppHostConnectionHelper.FindConnectionByAppHostPath(
            [connection],
            caseVariant);

        Assert.Equal(File.Exists(caseVariant), result is not null);
    }

    [Fact]
    public void AppHostPathComparer_WithDistinctCaseSensitivePaths_DoesNotUseCaseInsensitiveFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var lowercaseDirectory = workspace.WorkspaceRoot.CreateSubdirectory("case-sensitive-apphost");
        var uppercaseDirectoryPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "CASE-SENSITIVE-APPHOST");

        Assert.SkipWhen(
            Directory.Exists(uppercaseDirectoryPath),
            "The current temporary filesystem does not allow case-distinct directories.");

        var uppercaseDirectory = Directory.CreateDirectory(uppercaseDirectoryPath);
        var lowercaseAppHostPath = Path.Combine(lowercaseDirectory.FullName, "AppHost.csproj");
        var uppercaseAppHostPath = Path.Combine(uppercaseDirectory.FullName, "AppHost.csproj");
        File.WriteAllText(lowercaseAppHostPath, "<Project />");
        File.WriteAllText(uppercaseAppHostPath, "<Project />");

        Assert.False(AppHostPathComparer.PathsEqual(
            lowercaseAppHostPath,
            uppercaseAppHostPath,
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AppHostPathComparer_WithExactText_UsesFastPathBeforeFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var missingPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "missing",
            "AppHost.csproj");

        Assert.True(AppHostPathComparer.PathsEqual(
            missingPath,
            missingPath,
            NeverEqualStringComparer.Instance));
    }

    [Fact]
    public void AppHostPathComparer_WhenOnlyOnePathCanonicalizes_DoesNotUseFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var existingPath = Path.Combine(workspace.WorkspaceRoot.FullName, "Existing.AppHost.csproj");
        var missingPath = Path.Combine(workspace.WorkspaceRoot.FullName, "Missing.AppHost.csproj");
        File.WriteAllText(existingPath, "<Project />");

        Assert.False(AppHostPathComparer.PathsEqual(
            existingPath,
            missingPath,
            AlwaysEqualStringComparer.Instance));
    }

    [Fact]
    public async Task FindConnectionByAppHostPath_WithWindowsCaseSensitiveDirectory_DoesNotMatchCaseVariant()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Per-directory case sensitivity is only available on Windows.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var caseSensitiveDirectory = workspace.WorkspaceRoot.CreateSubdirectory("case-sensitive");
        var (caseSensitivityEnabled, failureReason) = await TryEnableWindowsCaseSensitivityAsync(
            caseSensitiveDirectory.FullName,
            TestContext.Current.CancellationToken);

        Assert.SkipUnless(
            caseSensitivityEnabled,
            $"The environment could not enable per-directory case sensitivity: {failureReason}");

        var actualPath = Path.Combine(caseSensitiveDirectory.FullName, "CaseSensitive.AppHost.csproj");
        var caseVariant = Path.Combine(caseSensitiveDirectory.FullName, "casesensitive.apphost.csproj");
        File.WriteAllText(actualPath, "<Project />");

        Assert.SkipWhen(
            File.Exists(caseVariant),
            "The environment did not create a case-sensitive directory.");

        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = actualPath,
                ProcessId = 1
            }
        };

        var result = AppHostConnectionHelper.FindConnectionByAppHostPath(
            [connection],
            caseVariant);

        Assert.Null(result);
    }

    [Fact]
    public void AppHostPathComparer_ObservesRetargetedSymlink()
    {
        Assert.SkipUnless(
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink mutation test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstTarget = Path.Combine(workspace.WorkspaceRoot.FullName, "first.csproj");
        var secondTarget = Path.Combine(workspace.WorkspaceRoot.FullName, "second.csproj");
        File.WriteAllText(firstTarget, "<Project />");
        File.WriteAllText(secondTarget, "<Project />");
        var linkPath = Path.Combine(workspace.WorkspaceRoot.FullName, "current.csproj");
        File.CreateSymbolicLink(linkPath, firstTarget);

        Assert.True(AppHostPathComparer.PathsEqual(linkPath, firstTarget));

        File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, secondTarget);

        // Canonical path results cannot be memoized: the same lexical path can identify
        // a different AppHost after an ordinary filesystem mutation.
        Assert.False(AppHostPathComparer.PathsEqual(linkPath, firstTarget));
        Assert.True(AppHostPathComparer.PathsEqual(linkPath, secondTarget));
    }

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

    private static async Task<(bool Success, string FailureReason)> TryEnableWindowsCaseSensitivityAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "fsutil.exe",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("file");
        startInfo.ArgumentList.Add("setCaseSensitiveInfo");
        startInfo.ArgumentList.Add(directoryPath);
        startInfo.ArgumentList.Add("enable");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "fsutil.exe did not start.");
            }

            // Read both streams concurrently because fsutil output can otherwise fill one redirected
            // pipe while the test is waiting for the process to exit.
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, $"fsutil.exe exited with code {process.ExitCode}. {standardOutput} {standardError}".Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private sealed class AlwaysEqualStringComparer : StringComparer
    {
        public static AlwaysEqualStringComparer Instance { get; } = new();

        public override int Compare(string? x, string? y) => 0;

        public override bool Equals(string? x, string? y) => true;

        public override int GetHashCode(string obj) => 0;
    }

    private sealed class NeverEqualStringComparer : StringComparer
    {
        public static NeverEqualStringComparer Instance { get; } = new();

        public override int Compare(string? x, string? y) => string.CompareOrdinal(x, y);

        public override bool Equals(string? x, string? y) => false;

        public override int GetHashCode(string obj) => 0;
    }
}
