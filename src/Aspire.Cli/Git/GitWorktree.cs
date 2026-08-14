// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Cli.Git;

/// <summary>
/// Detects git linked worktrees from the filesystem without spawning git.
/// </summary>
/// <remarks>
/// Git stores linked-worktree metadata in these shapes:
/// <code>
/// Standard:   /repo/.git/worktrees/feature
/// Bare:       /repo.git/worktrees/feature
/// Separate:   /separate-git/worktrees/feature
/// Submodule:  /repo/.git/worktrees/feature/modules/dependency
///
/// /checkout/.git:
/// gitdir: /repo/.git/worktrees/feature
///
/// /repo/.git/worktrees/feature/gitdir:
/// /checkout/.git
/// </code>
/// The admin directory's <c>gitdir</c> back-pointer distinguishes a real linked worktree
/// from stale metadata, while requiring its direct parent to be <c>worktrees</c> excludes
/// submodules nested under a linked worktree's <c>modules</c> directory.
/// See <see href="https://git-scm.com/docs/git-worktree">Git worktree documentation</see>.
/// </remarks>
internal static class GitWorktree
{
    private const int MaxAncestorWalks = 64;
    private const string GitDirPrefix = "gitdir:";
    private const string GitDirFileName = "gitdir";
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

            if (File.Exists(gitPath))
            {
                return IsLinkedWorktreeGitFile(gitPath, current)
                    ? PathNormalizer.ResolveSymlinks(current)
                    : null;
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
        if (!TryReadGitDirTarget(gitFilePath, worktreeRoot, out var adminDirectory) ||
            !Directory.Exists(adminDirectory))
        {
            return false;
        }

        var canonicalAdminDirectory = CanonicalizePath(adminDirectory);
        var adminParent = Directory.GetParent(Path.TrimEndingDirectorySeparator(canonicalAdminDirectory));
        var worktreesComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (adminParent is null ||
            !adminParent.Name.Equals(WorktreesSegment, worktreesComparison))
        {
            return false;
        }

        if (!TryReadPath(
            Path.Combine(adminDirectory, GitDirFileName),
            adminDirectory,
            out var checkoutGitFile))
        {
            return false;
        }

        return PathsEqual(checkoutGitFile, gitFilePath);
    }

    private static bool TryReadGitDirTarget(string gitFilePath, string worktreeRoot, out string gitDirectory)
    {
        gitDirectory = string.Empty;
        if (!TryReadFile(gitFilePath, out var contents))
        {
            return false;
        }

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

            return TryResolvePath(gitDir, worktreeRoot, out gitDirectory);
        }

        return false;
    }

    private static bool TryReadPath(string filePath, string relativeTo, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (!TryReadFile(filePath, out var contents))
        {
            return false;
        }

        var value = contents.Trim();
        return value.Length > 0 && TryResolvePath(value, relativeTo, out resolvedPath);
    }

    private static bool TryReadFile(string filePath, out string contents)
    {
        try
        {
            contents = File.ReadAllText(filePath);
            return true;
        }
        catch (IOException)
        {
            contents = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            contents = string.Empty;
            return false;
        }
    }

    private static bool TryResolvePath(string value, string relativeTo, out string resolvedPath)
    {
        try
        {
            resolvedPath = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(relativeTo, value));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            CanonicalizePath(left),
            CanonicalizePath(right),
            comparison);
    }

    private static string CanonicalizePath(string path)
    {
        var resolvedPath = PathNormalizer.ResolveSymlinks(path);
        if (!OperatingSystem.IsMacOS())
        {
            return resolvedPath;
        }

        var root = Path.GetPathRoot(resolvedPath);
        if (string.IsNullOrEmpty(root))
        {
            return resolvedPath;
        }

        var segments = resolvedPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return resolvedPath;
            }

            try
            {
                string? exactMatch = null;
                string? caseInsensitiveMatch = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var entryName = Path.GetFileName(entry);
                    if (entryName.Equals(segment, StringComparison.Ordinal))
                    {
                        exactMatch = entry;
                        break;
                    }

                    if (caseInsensitiveMatch is null &&
                        entryName.Equals(segment, StringComparison.OrdinalIgnoreCase))
                    {
                        caseInsensitiveMatch = entry;
                    }
                }

                if (exactMatch is not null)
                {
                    current = exactMatch;
                }
                else if (caseInsensitiveMatch is not null)
                {
                    // Path APIs preserve the caller's casing. Recover the stored casing only
                    // when the filesystem resolved the variant, so case-sensitive volumes still
                    // reject stale paths that differ only by case.
                    current = caseInsensitiveMatch;
                }
                else
                {
                    current = candidate;
                }
            }
            catch (IOException)
            {
                return resolvedPath;
            }
            catch (UnauthorizedAccessException)
            {
                return resolvedPath;
            }
        }

        return current;
    }
}
