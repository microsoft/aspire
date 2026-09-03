// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

internal static class PathNormalizer
{
    public static string NormalizePathForCurrentPlatform(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Fix slashes
        path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Normalizes a path for storage in configuration files by replacing
    /// backslash separators with forward slashes.
    /// </summary>
    public static string NormalizePathForStorage(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves a path to its filesystem-canonical form by resolving symbolic links and querying
    /// the OS for the actual casing of each path component on Windows and macOS.
    /// </summary>
    /// <remarks>
    /// Use this when aliases on a case-insensitive filesystem must produce the same identity while
    /// preserving distinct paths on case-sensitive macOS volumes. A user who types
    /// <c>--apphost c:\FOO\bar.csproj</c> will get back <c>C:\foo\bar.csproj</c>
    /// if that is the on-disk casing.
    /// </remarks>
    /// <param name="path">An absolute path to canonicalize.</param>
    /// <returns>
    /// The filesystem-canonical path. If the path cannot be fully resolved, returns a best-effort
    /// result containing any canonicalized prefix followed by the remaining unresolved segments.
    /// </returns>
    public static string ResolveToFilesystemPath(string path)
    {
        return ResolvePathCasing(ResolveSymlinks(path));
    }

    /// <summary>
    /// Resolves the casing of each path component without resolving symbolic links.
    /// </summary>
    /// <param name="path">An absolute path whose casing should be resolved.</param>
    /// <returns>The path with filesystem casing, or <paramref name="path"/> if it cannot be resolved.</returns>
    public static string ResolvePathCasing(string path)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return path;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var segments = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // Windows APIs preserve the caller's drive-letter casing even while directory enumeration
        // recovers every later component. Normalize the drive root so c:\foo and C:\foo produce
        // the same canonical path and callers receive the conventional on-disk form.
        var current = OperatingSystem.IsWindows() && root.Length >= 2 && root[1] == ':'
            ? $"{char.ToUpperInvariant(root[0])}{root[1..]}"
            : root;
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return path;
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

                current = exactMatch ?? caseInsensitiveMatch ?? candidate;
            }
            catch (IOException)
            {
                return path;
            }
            catch (UnauthorizedAccessException)
            {
                return path;
            }
        }

        return current;
    }

    /// <summary>
    /// Resolves symbolic links along every segment of <paramref name="path"/> and returns
    /// the filesystem-canonical absolute path. Useful for comparing two user-supplied paths
    /// that may differ only because one of them traverses a symlinked directory
    /// (for example <c>/tmp/x</c> vs <c>/private/tmp/x</c> on macOS, where <c>/tmp</c> is a
    /// symlink to <c>/private/tmp</c>).
    /// </summary>
    /// <remarks>
    /// <para>Walks each segment so that an <em>intermediate</em> directory symlink resolves
    /// correctly — <see cref="Directory.ResolveLinkTarget(string, bool)"/> only reads the
    /// symlink at exactly the path it is given, so a single call on a path like
    /// <c>/tmp/x/y.cs</c> would not unwrap <c>/tmp</c>.</para>
    /// <para>On any IO failure (broken link, permission denied, missing intermediate
    /// segment, circular link), returns the path with as many segments resolved as
    /// possible. This is a best-effort canonicalization for comparison — callers should
    /// not rely on it for security boundaries.</para>
    /// </remarks>
    public static string ResolveSymlinks(string path)
    {
        return ResolveSymlinksCore(path, depth: 0);
    }

    /// <summary>
    /// Attempts to resolve symbolic links along every segment of <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResolveSymlinks"/>, this method reports IO, permission, and circular-link failures
    /// instead of returning a partially canonicalized path. An ordinary missing segment that is not observable
    /// as a symbolic link remains lexical; callers must still account for filesystem changes after validation.
    /// </remarks>
    /// <param name="path">The path to canonicalize.</param>
    /// <param name="resolvedPath">The canonical path when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when every observable symbolic link was resolved; otherwise, <see langword="false"/>.</returns>
    public static bool TryResolveSymlinks(string path, out string resolvedPath)
    {
        return TryResolveSymlinksCore(path, depth: 0, out resolvedPath);
    }

    // Hard depth limit on recursive canonicalization to defend against pathological
    // symlink chains; well-formed real-world paths resolve in a handful of levels.
    private const int MaxResolveSymlinksDepth = 40;

    private static string ResolveSymlinksCore(string path, int depth)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (depth > MaxResolveSymlinksDepth)
        {
            // Give up rather than risk a stack overflow on circular/pathological links.
            return path;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return fullPath;
            }

            // Walk only the part after the root so segment splitting cannot eat a drive
            // letter ("C:") or UNC prefix.
            var relative = fullPath[root.Length..];
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);

                FileSystemInfo? linkTarget = null;
                try
                {
                    // For intermediate segments we know they must be directories — files
                    // cannot have child segments. For the final segment, try file first
                    // then directory, since either is plausible.
                    linkTarget = i < segments.Length - 1
                        ? Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                        : File.ResolveLinkTarget(current, returnFinalTarget: true)
                          ?? Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                }
                catch (IOException)
                {
                    // Broken or circular symlink. Stop unwrapping and return what we have
                    // resolved so far combined with the remaining unresolved segments —
                    // matches the behaviour callers get from FileInfo when the link is bad.
                    return CombineRemaining(current, segments, i + 1);
                }
                catch (UnauthorizedAccessException)
                {
                    return CombineRemaining(current, segments, i + 1);
                }

                if (linkTarget?.FullName is { Length: > 0 } resolved)
                {
                    // ResolveLinkTarget returns the symlink target exactly as stored on disk,
                    // which may itself contain unresolved symlinks in intermediate segments
                    // (for example on macOS a link target "/var/.../app" still has
                    // "/var -> /private/var" unresolved). Recurse so the canonical form does
                    // not depend on which side of the comparison reached the file first.
                    current = ResolveSymlinksCore(resolved, depth + 1);
                }
            }

            return current;
        }
        catch (Exception)
        {
            // Defensive: any unexpected normalization failure preserves caller-visible
            // behaviour by falling back to the input path.
            return path;
        }

        static string CombineRemaining(string current, string[] segments, int startIndex)
        {
            for (var j = startIndex; j < segments.Length; j++)
            {
                current = Path.Combine(current, segments[j]);
            }

            return current;
        }
    }

    private static bool TryResolveSymlinksCore(string path, int depth, out string resolvedPath)
    {
        resolvedPath = path;

        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        if (depth > MaxResolveSymlinksDepth)
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                resolvedPath = fullPath;
                return true;
            }

            var relative = fullPath[root.Length..];
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);

                var storedLinkTarget = new FileInfo(current).LinkTarget ?? new DirectoryInfo(current).LinkTarget;
                if (storedLinkTarget is null && !File.Exists(current) && !Directory.Exists(current))
                {
                    try
                    {
                        _ = File.GetAttributes(current);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        for (var j = i + 1; j < segments.Length; j++)
                        {
                            current = Path.Combine(current, segments[j]);
                        }

                        resolvedPath = current;
                        return true;
                    }
                }

                var linkTarget = i < segments.Length - 1
                    ? Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                    : File.ResolveLinkTarget(current, returnFinalTarget: true)
                      ?? Directory.ResolveLinkTarget(current, returnFinalTarget: true);

                if (linkTarget?.FullName is { Length: > 0 } resolved)
                {
                    if (!TryResolveSymlinksCore(resolved, depth + 1, out current))
                    {
                        return false;
                    }
                }
            }

            resolvedPath = current;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
