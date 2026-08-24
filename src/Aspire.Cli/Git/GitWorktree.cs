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
                return IsLinkedWorktreeGitFile(gitPath)
                    ? CanonicalizePath(current)
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
    /// Both paths are reduced to the scope identity described on
    /// <see cref="TryGetScopeWorktreeRoot"/>. Two paths share a scope only when both identities
    /// are non-<see langword="null"/> and equal, or when both are <see langword="null"/> (neither
    /// path is in a git repository at all). Because a primary checkout now identifies as its own
    /// root, running <c>aspire stop</c> in repository A cannot reach an AppHost in unrelated
    /// repository B, and a nested <c>.worktrees/feature</c> checkout still cannot be stolen by
    /// the primary checkout that contains it.
    /// </remarks>
    public static bool IsSameWorktreeScope(string appHostPath, string workingDirectory)
    {
        var workingScopeRoot = TryGetScopeWorktreeRoot(workingDirectory);
        var appHostScopeRoot = TryGetScopeWorktreeRoot(appHostPath);

        if (workingScopeRoot is null || appHostScopeRoot is null)
        {
            // "Not in a git repository" is only ever equivalent to another path that is also
            // outside every repository. Pairing it with a real checkout root would let an
            // unrelated stray path fall into a repository's scope.
            return workingScopeRoot is null && appHostScopeRoot is null;
        }

        return PathsEqual(workingScopeRoot, appHostScopeRoot);
    }

    /// <summary>
    /// Returns the canonical root of the checkout that owns <paramref name="startPath"/>, used as
    /// the scope identity for stop/ps filtering.
    /// </summary>
    /// <remarks>
    /// The identity is three-way and each case must stay distinguishable:
    /// <list type="bullet">
    /// <item><description>inside a linked worktree: the canonical linked worktree root.</description></item>
    /// <item><description>inside a primary checkout: the canonical primary checkout root.</description></item>
    /// <item><description>not inside any git repository: <see langword="null"/>.</description></item>
    /// </list>
    /// Collapsing the second and third cases to <see langword="null"/> would make every primary
    /// checkout compare equal to every other primary checkout and to every non-git directory.
    /// </remarks>
    private static string? TryGetScopeWorktreeRoot(string startPath)
    {
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
                // Primary checkout (or any clone with a real .git directory). Its own root is the
                // scope identity so two unrelated clones never compare equal.
                return CanonicalizePath(current);
            }

            if (File.Exists(gitPath))
            {
                if (IsLinkedWorktreeGitFile(gitPath))
                {
                    return CanonicalizePath(current);
                }

                // A submodule's .git is a file whose gitdir points into the superproject:
                //   /repo/extern/dep/.git                -> gitdir: /repo/.git/modules/dep
                //   /repo/.worktrees/feature/extern/dep/.git
                //                                        -> gitdir: /repo/.git/worktrees/feature/modules/dep
                // A submodule is a separate checkout, but for scoping it belongs to whatever
                // checkout physically contains it: the enclosing linked worktree when the admin
                // directory sits under <common>/worktrees/<id>/, ...
                if (TryReadGitDirTarget(gitPath, out var gitDirectory) &&
                    TryGetEnclosingLinkedWorktreeRoot(gitDirectory) is { } enclosingWorktreeRoot)
                {
                    return enclosingWorktreeRoot;
                }

                // ... otherwise keep walking ancestors so a submodule in a primary checkout
                // resolves to that checkout's root instead of collapsing to null, which would
                // make a submodule of repository A compare equal to a submodule of repository B.
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

    private static bool IsLinkedWorktreeGitFile(string gitFilePath)
    {
        if (!TryReadGitDirTarget(gitFilePath, out var adminDirectory) ||
            !Directory.Exists(adminDirectory))
        {
            return false;
        }

        var canonicalAdminDirectory = CanonicalizePath(adminDirectory);
        var adminParent = Directory.GetParent(Path.TrimEndingDirectorySeparator(canonicalAdminDirectory));
        if (!IsWorktreesDirectory(adminParent))
        {
            return false;
        }

        // Git resolves this back-pointer from the physical admin directory, even when
        // the checkout's .git file reached that directory through an alias.
        if (!TryReadPath(
            Path.Combine(canonicalAdminDirectory, GitDirFileName),
            canonicalAdminDirectory,
            out var checkoutGitFile))
        {
            return false;
        }

        return PathsEqual(checkoutGitFile, gitFilePath);
    }

    private static string? TryGetEnclosingLinkedWorktreeRoot(string gitDirectory)
    {
        var current = new DirectoryInfo(CanonicalizePath(gitDirectory));
        for (var i = 0; i < MaxAncestorWalks && current.Parent is not null; i++)
        {
            if (IsWorktreesDirectory(current.Parent) &&
                TryReadPath(
                    Path.Combine(current.FullName, GitDirFileName),
                    current.FullName,
                    out var checkoutGitFile) &&
                File.Exists(checkoutGitFile) &&
                IsLinkedWorktreeGitFile(checkoutGitFile))
            {
                return Path.GetDirectoryName(CanonicalizePath(checkoutGitFile));
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsWorktreesDirectory(DirectoryInfo? directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return directory?.Name.Equals(WorktreesSegment, comparison) is true;
    }

    private static bool TryReadGitDirTarget(string gitFilePath, out string gitDirectory)
    {
        gitDirectory = string.Empty;
        // Relative gitdir values are based on the physical directory containing this
        // metadata file, not the lexical checkout alias used to discover it.
        var canonicalGitFilePath = CanonicalizePath(gitFilePath);
        var metadataDirectory = Path.GetDirectoryName(canonicalGitFilePath);
        if (string.IsNullOrEmpty(metadataDirectory) ||
            !TryReadFile(canonicalGitFilePath, out var contents))
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

            return TryResolvePath(gitDir, metadataDirectory, out gitDirectory);
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
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
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
