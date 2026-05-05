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
}
