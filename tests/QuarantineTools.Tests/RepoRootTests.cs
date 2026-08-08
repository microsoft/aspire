// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace QuarantineTools.Tests;

/// <summary>
/// Covers repository-root resolution. The tool rewrites source files in bulk, so resolving the wrong
/// root silently edits a different checkout instead of failing.
/// </summary>
public class RepoRootTests : IDisposable
{
    private readonly DirectoryInfo _scratch;

    public RepoRootTests()
    {
        // Canonicalize up front so every derived path is already symlink-free. On macOS the temp
        // directory lives under /var, which is a symlink to /private/var, and git reports the resolved
        // form - comparing the two without this would fail for reasons unrelated to root resolution.
        _scratch = new DirectoryInfo(Canonicalize(Directory.CreateTempSubdirectory("quarantine-reporoot-").FullName));
    }

    public void Dispose()
    {
        // Cleanup must never throw: `CreateRepositoryWithNestedWorktree` can fail partway through, and a
        // failure in here would replace the real assertion failure with a confusing teardown error.
        try
        {
            // The nested worktree must be unregistered before the outer repo is deleted, otherwise git
            // leaves administrative files behind under .git/worktrees.
            var outer = Path.Combine(_scratch.FullName, "outer");
            if (Directory.Exists(Path.Combine(outer, "nested")))
            {
                RunGit(outer, "worktree", "remove", "--force", "nested");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Xunit.Sdk.XunitException)
        {
            // Best effort - fall through to deleting the directory tree outright.
        }

        try
        {
            _scratch.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover temp directory must not fail the test run.
        }
    }

    [Fact]
    public async Task FindRepoRoot_FromLinkedWorktree_ReturnsWorktreeRoot_NotOuterCheckout()
    {
        var (_, nested) = CreateOuterRepoWithNestedWorktree();

        var resolved = await Program.FindRepoRootAsync(nested, TestContext.Current.CancellationToken);

        Assert.Equal(Path.TrimEndingDirectorySeparator(nested), Path.TrimEndingDirectorySeparator(resolved!));
    }

    [Fact]
    public async Task FindRepoRoot_FromSubdirectoryOfLinkedWorktree_ReturnsWorktreeRoot()
    {
        var (_, nested) = CreateOuterRepoWithNestedWorktree();
        var deep = Directory.CreateDirectory(Path.Combine(nested, "tests", "Sample", "deep")).FullName;

        var resolved = await Program.FindRepoRootAsync(deep, TestContext.Current.CancellationToken);

        Assert.Equal(Path.TrimEndingDirectorySeparator(nested), Path.TrimEndingDirectorySeparator(resolved!));
    }

    [Fact]
    public async Task FindRepoRoot_FromMainCheckout_ReturnsMainCheckoutRoot()
    {
        var (outer, _) = CreateOuterRepoWithNestedWorktree();

        var resolved = await Program.FindRepoRootAsync(outer, TestContext.Current.CancellationToken);

        Assert.Equal(Path.TrimEndingDirectorySeparator(outer), Path.TrimEndingDirectorySeparator(resolved!));
    }

    [Fact]
    public async Task FindRepoRoot_OutsideAnyRepository_ReturnsNull()
    {
        var loose = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "loose")).FullName;

        var resolved = await Program.FindRepoRootAsync(loose, TestContext.Current.CancellationToken);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("/a/b", "/a/b", true)]
    [InlineData("/a", "/a/b/c", true)]
    [InlineData("/a/b/c", "/a", false)]
    [InlineData("/a/bb", "/a/b", false)]
    public void IsSameOrAncestorDirectory_MatchesOnPathSegments(string ancestor, string directory, bool expected)
    {
        // Windows cannot resolve rooted POSIX paths, so anchor the cases under the scratch directory.
        var a = Path.GetFullPath(Path.Combine(_scratch.FullName, ancestor.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        var d = Path.GetFullPath(Path.Combine(_scratch.FullName, directory.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(expected, Program.IsSameOrAncestorDirectory(a, d));
    }

    [Fact]
    public void IsSameOrAncestorDirectory_CaseOnlyDifference_IsRejectedOnACaseSensitiveVolume()
    {
        // Two distinct trees whose paths differ only by case are the exact cross-tree write this guard
        // exists to block, so folding case here would approve the wrong root.
        var ancestor = Path.Combine(_scratch.FullName, "OUTER");
        var directory = Path.Combine(_scratch.FullName, "outer", "tests");

        Assert.False(Program.IsSameOrAncestorDirectory(ancestor, directory, caseSensitive: true));
    }

    [Fact]
    public void IsSameOrAncestorDirectory_CaseOnlyDifference_IsAcceptedOnACaseInsensitiveVolume()
    {
        // The mirrored case. git canonicalizes --show-toplevel to the on-disk casing; getcwd(3) does the
        // same on Unix, but the Windows current directory keeps the casing the process was given, so a
        // difference that means nothing can reach this guard and must not be refused.
        var ancestor = Path.Combine(_scratch.FullName, "OUTER");
        var directory = Path.Combine(_scratch.FullName, "outer", "tests");

        Assert.True(Program.IsSameOrAncestorDirectory(ancestor, directory, caseSensitive: false));
    }

    [Fact]
    public void IsCaseSensitiveDirectory_AgreesWithTheFilesystemItProbes()
    {
        // Self-validating on any host: whatever this volume actually does, the probe must report it.
        var probed = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "CaseProbe")).FullName;
        var reachableThroughAnotherCasing = Directory.Exists(Path.Combine(_scratch.FullName, "caseprobe"));

        Assert.Equal(!reachableThroughAnotherCasing, Program.IsCaseSensitiveDirectory(probed));
    }

    [Fact]
    public void IsSameOrAncestorDirectory_UsesEachParentsCasingRules_WhenTheFinalSegmentProbeIsAmbiguous()
    {
        var parent = Path.Combine(_scratch.FullName, "case-sensitive-parent");
        var upperRepo = Path.Combine(parent, "Outer", "repo");
        var lowerRepoTests = Path.Combine(parent, "outer", "repo", "tests");

        Assert.False(Program.IsSameOrAncestorDirectory(
            upperRepo,
            lowerRepoTests,
            probedDirectory => string.Equals(
                Path.TrimEndingDirectorySeparator(probedDirectory),
                Path.TrimEndingDirectorySeparator(parent),
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FindRepoRoot_WhenTheCallerCancels_PropagatesCancellationInsteadOfFallingBack()
    {
        var (_, nested) = CreateOuterRepoWithNestedWorktree();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Swallowing this would run the fallback walk and let ExecuteAsync enumerate the whole tests
        // tree before cancellation is next observed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Program.FindRepoRootAsync(nested, cancelled.Token));
    }

    /// <summary>
    /// The <c>.git</c>-as-a-file branch is where the bug lived, but the git probe answers first and hides
    /// it. This drives the fallback walk directly, with no git process involved.
    /// </summary>
    [Fact]
    public void FindRepoRootByMarker_StopsAtWorktreeGitFile_NotOuterGitDirectory()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "outer")).FullName;
        Directory.CreateDirectory(Path.Combine(outer, ".git"));

        var worktree = Directory.CreateDirectory(Path.Combine(outer, ".worktrees", "linked")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(outer, ".git", "worktrees", "linked")}\n");

        var nested = Directory.CreateDirectory(Path.Combine(worktree, "tools")).FullName;

        Assert.Equal(worktree, Program.FindRepoRootByMarker(nested));
    }

    [Fact]
    public void FindRepoRootByMarker_StopsAtGitDirectory_WhenNoWorktreeInvolved()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "outer")).FullName;
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(outer, "tools", "deep")).FullName;

        Assert.Equal(outer, Program.FindRepoRootByMarker(nested));
    }

    [Fact]
    public void FindRepoRootByMarker_ReturnsNull_WhenNoMarkerExists()
    {
        var loose = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "loose", "deep")).FullName;

        Assert.Null(Program.FindRepoRootByMarker(loose));
    }

    [Fact]
    public void WrongTreeError_IsNull_WhenRootIsAnAncestor()
    {
        var root = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "repo")).FullName;
        var inside = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;

        Assert.Null(Program.TryGetWrongTreeError(root, inside));
    }

    /// <summary>
    /// The guard exists because a wrong-tree run is otherwise indistinguishable from a correct one, so
    /// the message has to name both paths. Pin that so a later reword cannot quietly drop one.
    /// </summary>
    [Fact]
    public void WrongTreeError_NamesBothResolvedRootAndCurrentDirectory()
    {
        var root = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "repoA")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "repoB", "tools")).FullName;

        var error = Program.TryGetWrongTreeError(root, elsewhere);

        Assert.NotNull(error);
        Assert.Contains(root, error);
        Assert.Contains(elsewhere, error);
    }

    /// <summary>
    /// A junction or `subst` drive on Windows lets the caller's directory and git's answer name the same
    /// tree with different spellings. Refusing that would block a legitimate run, so the guard resolves
    /// links before it gives up. Symlink creation needs elevation on Windows, so this is POSIX-only.
    /// </summary>
    [Fact]
    public void WrongTreeError_IsNull_WhenPathsDifferOnlyBySymlink()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "creating symlinks requires elevation on Windows");

        // The link target is relative AND routed through a second link, so resolving one hop hands back
        // another unresolved spelling. Anything that substitutes a target without re-resolving it fails.
        var realBase = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "realbase")).FullName;
        var realRepo = Directory.CreateDirectory(Path.Combine(realBase, "repo")).FullName;
        var inside = Directory.CreateDirectory(Path.Combine(realRepo, "tools")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(_scratch.FullName, "linkbase"), "realbase");
        Directory.CreateSymbolicLink(Path.Combine(_scratch.FullName, "repolink"), Path.Combine("linkbase", "repo"));

        var viaLink = Path.Combine(_scratch.FullName, "repolink", "tools");

        Assert.False(Program.IsSameOrAncestorDirectory(realRepo, viaLink), "the two spellings should differ textually, otherwise this test proves nothing");
        Assert.Null(Program.TryGetWrongTreeError(realRepo, viaLink));
        Assert.Null(Program.TryGetWrongTreeError(realRepo, inside));
    }

    private static bool IsGitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Tool_RefusesWithTheWrongTreeExitCode_AndLeavesTheOtherTreeUntouched()
    {
        Assert.SkipUnless(IsGitAvailable(), "git is not available on PATH");

        // A stale GIT_DIR/GIT_WORK_TREE is the case this guard is for: git answers with a tree the
        // caller is not standing in, so every edit would land there. It is not what fixes the reported
        // nested-worktree bug, where the outer checkout genuinely is an ancestor of the caller.
        var elsewhere = CreateRepositoryWithASampleTest("elsewhere");
        var here = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "here")).FullName;
        var sample = Path.Combine(elsewhere, "tests", "Sample", "SampleTests.cs");
        var before = await File.ReadAllTextAsync(sample, TestContext.Current.CancellationToken);

        var (exitCode, standardError) = await RunToolAsync(here, new Dictionary<string, string>
        {
            ["GIT_DIR"] = Path.Combine(elsewhere, ".git"),
            ["GIT_WORK_TREE"] = elsewhere,
        });

        Assert.Equal(Program.ExitCodeWrongTree, exitCode);
        Assert.Contains(elsewhere, standardError, StringComparison.Ordinal);
        Assert.Contains(here, standardError, StringComparison.Ordinal);

        // The payload assertion. Everything else here checks how the refusal is reported; this checks
        // that the other tree was actually left alone.
        Assert.Equal(before, await File.ReadAllTextAsync(sample, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Creates a git repository containing a single sample test file.
    /// </summary>
    private string CreateRepositoryWithASampleTest(string name)
    {
        var repository = Directory.CreateDirectory(Path.Combine(_scratch.FullName, name)).FullName;
        Directory.CreateDirectory(Path.Combine(repository, "tests", "Sample"));
        File.WriteAllText(
            Path.Combine(repository, "tests", "Sample", "SampleTests.cs"),
            "namespace N; public class C { public void M() { } }");

        RunGit(repository, "init", "-q", "-b", "main", ".");
        return repository;
    }

    /// <summary>
    /// Runs the built tool in <paramref name="workingDirectory"/>. The environment is applied to the
    /// child process only, so this stays safe under parallel test execution.
    /// </summary>
    private static async Task<(int ExitCode, string StandardError)> RunToolAsync(string workingDirectory, IDictionary<string, string> environment)
    {
        // The tool is a project reference, so its assembly and apphost sit in this test's output folder.
        var toolAssembly = typeof(Program).Assembly.Location;
        var appHost = Path.ChangeExtension(toolAssembly, OperatingSystem.IsWindows() ? ".exe" : null);
        var useAppHost = File.Exists(appHost);

        var startInfo = new ProcessStartInfo(useAppHost ? appHost : "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!useAppHost)
        {
            startInfo.ArgumentList.Add(toolAssembly);
        }

        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("https://github.com/microsoft/aspire/issues/1234");
        startInfo.ArgumentList.Add("N.C.M");

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the tool.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(standardErrorTask, standardOutputTask);

        return (process.ExitCode, standardErrorTask.Result);
    }

    /// <summary>
    /// Creates a main checkout with a linked worktree nested inside it. The nesting is the point: the
    /// worktree's <c>.git</c> is a file, so a probe that only looks for a <c>.git</c> directory walks
    /// past it and lands on the outer checkout.
    /// </summary>
    private (string Outer, string Nested) CreateOuterRepoWithNestedWorktree()
    {
        Assert.SkipUnless(IsGitAvailable(), "git is not available on PATH");

        var outer = Directory.CreateDirectory(Path.Combine(_scratch.FullName, "outer")).FullName;
        Directory.CreateDirectory(Path.Combine(outer, "tests", "Sample"));
        File.WriteAllText(Path.Combine(outer, "tests", "Sample", "SampleTests.cs"), "namespace N; public class C { public void M() { } }");

        RunGit(outer, "init", "-q", "-b", "main", ".");
        RunGit(outer, "config", "user.email", "test@example.com");
        RunGit(outer, "config", "user.name", "test");
        RunGit(outer, "config", "commit.gpgsign", "false");
        RunGit(outer, "add", "-A");
        RunGit(outer, "commit", "-q", "-m", "seed");

        var nested = Path.Combine(outer, "nested");
        RunGit(outer, "worktree", "add", "-q", "-f", nested, "-b", "nested-branch", "HEAD");

        Assert.True(File.Exists(Path.Combine(nested, ".git")), "linked worktree should have a .git file, not a directory");
        Assert.True(Directory.Exists(Path.Combine(outer, ".git")), "main checkout should have a .git directory");

        return (outer, nested);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr.Result}{stdout.Result}");
    }

    /// <summary>
    /// Resolves every symlinked component of <paramref name="path"/>. <see cref="FileSystemInfo.ResolveLinkTarget"/>
    /// only resolves the final component, so ancestors are walked explicitly.
    /// </summary>
    private static string Canonicalize(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));

        if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            return Canonicalize(target.FullName);
        }

        return info.Parent is { } parent
            ? Path.Combine(Canonicalize(parent.FullName), info.Name)
            : Path.TrimEndingDirectorySeparator(info.FullName);
    }
}
