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
    /// On Windows, resolves a path to its filesystem-canonical form by querying the OS for
    /// the actual casing of each path component. On other platforms this is a no-op because
    /// the file system is case-sensitive and there is no casing ambiguity.
    /// </summary>
    /// <remarks>
    /// Use this when the path needs to match what MSBuild reports for the same file.
    /// MSBuild always uses the true filesystem casing; a user who types
    /// <c>--apphost c:\FOO\bar.csproj</c> will get back <c>C:\foo\bar.csproj</c>
    /// if that is the on-disk casing, making the hash agree with the AppHost side.
    /// </remarks>
    /// <param name="path">An absolute path to a file that exists on disk.</param>
    /// <returns>
    /// The path with OS-canonical casing, or <paramref name="path"/> unchanged if it
    /// cannot be resolved (file does not exist, UNC path, etc.).
    /// </returns>
    public static string ResolveToFilesystemPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        // Only handle standard drive-letter paths (e.g. C:\...).
        // UNC paths (\\server\share\...) are not common for project files and are left unchanged.
        if (path.Length < 3 || path[1] != ':' || path[2] != Path.DirectorySeparatorChar)
        {
            return path;
        }

        // Uppercase the drive letter and use it as the starting root (e.g. "C:\").
        var current = char.ToUpperInvariant(path[0]) + ":" + Path.DirectorySeparatorChar;

        // Walk each component after the root ("X:\") to resolve its real casing.
        var parts = path[3..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
            {
                // Final component: find the file with its real name.
                var files = Directory.GetFiles(current, parts[i]);
                return files.Length == 1 ? files[0] : Path.Combine(current, parts[i]);
            }

            // Intermediate component: find the directory with its real name.
            var dirs = Directory.GetDirectories(current, parts[i]);
            current = dirs.Length == 1 ? dirs[0] : Path.Combine(current, parts[i]);

            if (!current.EndsWith(Path.DirectorySeparatorChar))
            {
                current += Path.DirectorySeparatorChar;
            }
        }

        return path;
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
}
