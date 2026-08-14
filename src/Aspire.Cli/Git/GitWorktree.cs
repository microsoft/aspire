// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Cli.Git;

/// <summary>
/// Detects git linked worktrees from the filesystem without spawning git.
/// </summary>
/// <remarks>
/// A linked worktree stores a <c>.git</c> file whose <c>gitdir:</c> line points at
/// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>. The primary checkout has a <c>.git</c>
/// directory. Submodules also use a <c>.git</c> file, but their gitdir points at
/// <c>.git/modules/</c> and must not be treated as linked worktrees.
/// See https://git-scm.com/docs/git-worktree
/// </remarks>
internal static class GitWorktree
{
    private const int MaxAncestorWalks = 64;
    private const string GitDirPrefix = "gitdir:";
    private const string GitDirectoryName = ".git";
    private const string WorktreesSegment = "worktrees";

    /// <summary>
    /// Returns the root of the linked worktree that contains <paramref name="startPath"/>,
    /// or <c>null</c> when the path is in the primary checkout, a submodule, or not a git repo.
    /// </summary>
    public static string? TryGetLinkedWorktreeRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        string current;
        try
        {
            current = GetWalkStartDirectory(startPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        for (var i = 0; i < MaxAncestorWalks; i++)
        {
            var gitPath = Path.Combine(current, GitDirectoryName);

            if (Directory.Exists(gitPath))
            {
                // Primary checkout (or any clone with a .git directory). Stop so a nested
                // path cannot be classified as a worktree of an ancestor repo.
                return null;
            }

            if (File.Exists(gitPath) && IsLinkedWorktreeGitFile(gitPath, current))
            {
                return PathNormalizer.ResolveSymlinks(current);
            }

            var parent = Directory.GetParent(current);
            if (parent is null || string.Equals(parent.FullName, current, StringComparison.Ordinal))
            {
                return null;
            }

            current = parent.FullName;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="appHostPath"/> should be treated as in the same git worktree
    /// as <paramref name="workingDirectory"/> for stop/ps scoping.
    /// </summary>
    /// <remarks>
    /// A linked worktree cwd only matches AppHosts in that same worktree. A primary (or
    /// non-git) cwd matches AppHosts that are not inside a different linked worktree, so a
    /// nested <c>.worktrees/feature</c> checkout cannot steal <c>aspire stop</c>.
    /// </remarks>
    public static bool IsSameWorktreeScope(string appHostPath, string workingDirectory)
    {
        var workingLinkedRoot = TryGetLinkedWorktreeRoot(workingDirectory);
        var appHostLinkedRoot = TryGetLinkedWorktreeRoot(appHostPath);

        if (workingLinkedRoot is not null)
        {
            return appHostLinkedRoot is not null && PathsEqual(workingLinkedRoot, appHostLinkedRoot);
        }

        return appHostLinkedRoot is null;
    }

    private static string GetWalkStartDirectory(string startPath)
    {
        var fullPath = Path.GetFullPath(startPath);
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        var directory = Path.GetDirectoryName(fullPath);
        return string.IsNullOrEmpty(directory) ? fullPath : directory;
    }

    private static bool IsLinkedWorktreeGitFile(string gitFilePath, string worktreeRoot)
    {
        string contents;
        try
        {
            contents = File.ReadAllText(gitFilePath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // Linked worktree .git files look like:
        // gitdir: /repo/.git/worktrees/feature
        // gitdir: ../.git/worktrees/feature
        // Submodule files use gitdir: .../.git/modules/<name> and must not match.
        foreach (var rawLine in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var gitDir = line[GitDirPrefix.Length..].Trim();
            if (gitDir.Length == 0)
            {
                return false;
            }

            string absoluteGitDir;
            try
            {
                absoluteGitDir = Path.IsPathRooted(gitDir)
                    ? Path.GetFullPath(gitDir)
                    : Path.GetFullPath(Path.Combine(worktreeRoot, gitDir));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return false;
            }

            return ContainsGitWorktreesSegment(absoluteGitDir);
        }

        return false;
    }

    private static bool ContainsGitWorktreesSegment(string gitDirPath)
    {
        var segments = gitDirPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].Equals(WorktreesSegment, StringComparison.OrdinalIgnoreCase) &&
                segments[i - 1].Equals(GitDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            PathNormalizer.ResolveSymlinks(left),
            PathNormalizer.ResolveSymlinks(right),
            comparison);
    }
}
