// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Utils;

/// <summary>
/// Helper class for file system operations.
/// </summary>
internal static class FileSystemHelper
{
    /// <summary>
    /// Copies an entire directory and its contents to a new location.
    /// </summary>
    /// <param name="sourceDir">The source directory to copy from.</param>
    /// <param name="destinationDir">The destination directory to copy to.</param>
    /// <param name="overwrite">Whether to overwrite existing files in the destination directory.</param>
    internal static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDir);
        ArgumentException.ThrowIfNullOrEmpty(destinationDir);

        var sourceDirInfo = new DirectoryInfo(sourceDir);
        if (!sourceDirInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        // Use a stack to avoid recursion and potential stack overflow with deep directory structures
        var stack = new Stack<(DirectoryInfo Source, string Destination)>();
        stack.Push((sourceDirInfo, destinationDir));

        while (stack.Count > 0)
        {
            var (currentSource, currentDestination) = stack.Pop();

            // Create the destination directory if it doesn't exist
            Directory.CreateDirectory(currentDestination);

            // Copy all files in the current directory
            foreach (var file in currentSource.GetFiles())
            {
                var targetFilePath = Path.Combine(currentDestination, file.Name);
                file.CopyTo(targetFilePath, overwrite);
            }

            // Push all subdirectories onto the stack
            foreach (var subDir in currentSource.GetDirectories())
            {
                var targetSubDir = Path.Combine(currentDestination, subDir.Name);
                stack.Push((subDir, targetSubDir));
            }
        }
    }

    /// <summary>
    /// Recursively searches for the first file matching any of the given patterns.
    /// Stops immediately when a match is found.
    /// </summary>
    /// <param name="root">Root folder to start search</param>
    /// <param name="recurseLimit">Maximum directory depth to search. Use 0 to search only the root, or -1 for unlimited depth.</param>
    /// <param name="patterns">File name patterns, e.g., "*.csproj", "apphost.cs"</param>
    /// <returns>Full path to first matching file, or null if none found</returns>
    public static string? FindFirstFile(string root, int recurseLimit = -1, params string[] patterns)
    {
        if (!Directory.Exists(root) || patterns.Length == 0)
        {
            return null;
        }

        var dirs = new Stack<(string Path, int Depth)>();
        dirs.Push((root, 0));

        while (dirs.Count > 0)
        {
            var (dir, depth) = dirs.Pop();

            try
            {
                // Check for each pattern in this directory
                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.EnumerateFiles(dir, pattern))
                    {
                        return file; // first match, exit immediately
                    }
                }

                // Push subdirectories for further search if within depth limit
                if (recurseLimit < 0 || depth < recurseLimit)
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        dirs.Push((sub, depth + 1));
                    }
                }
            }
            catch
            {
                // Skip directories we can't access (permissions, etc.)
            }
        }

        return null;
    }

    /// <summary>
    /// Shortens a list of paths so each is uniquely identifiable using the minimum
    /// number of trailing path segments. Duplicate filenames get parent directories
    /// added until unique. Non-project files (e.g. single-file AppHosts like
    /// AppHost.cs) always include at least the parent folder to provide context.
    /// </summary>
    internal static Dictionary<string, string> ShortenPaths(IReadOnlyList<string> paths)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);

        if (paths.Count == 0)
        {
            return result;
        }

        // Split each path into normalized segments
        var segmentsMap = new Dictionary<string, string[]>(comparer);
        var depthMap = new Dictionary<string, int>(comparer);

        foreach (var path in paths)
        {
            if (result.ContainsKey(path))
            {
                continue; // Skip duplicate paths
            }

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var segments = normalized.Split('/');
            segmentsMap[path] = segments;

            // Non-project files (single-file AppHosts) always show parent/filename
            var fileName = segments.Length > 0 ? segments[^1] : path;
            var extension = Path.GetExtension(fileName);
            var isProjectFile = DotNetAppHostProject.ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
            var minDepth = !isProjectFile && segments.Length >= 2 ? 2 : 1;

            depthMap[path] = minDepth;
            result[path] = minDepth >= 2
                ? Path.Combine(segments[^2], segments[^1])
                : fileName;
        }

        // Iteratively resolve duplicates by adding more parent directory segments
        while (true)
        {
            var duplicateGroups = result
                .GroupBy(kvp => kvp.Value, comparer)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicateGroups.Count == 0)
            {
                break;
            }

            foreach (var group in duplicateGroups)
            {
                foreach (var kvp in group)
                {
                    var originalPath = kvp.Key;
                    var segments = segmentsMap[originalPath];
                    var newDepth = depthMap[originalPath] + 1;
                    depthMap[originalPath] = newDepth;

                    if (newDepth >= segments.Length)
                    {
                        // Use full path when all segments exhausted
                        result[originalPath] = originalPath;
                    }
                    else
                    {
                        var candidate = Path.Combine(segments[^newDepth..]);

                        // Switch to the full original path when the candidate itself
                        // would include a root/drive segment, to avoid displaying
                        // something like "C:\folder\Project.csproj" with a tilde prefix.
                        var firstCandidateIndex = segments.Length - newDepth;
                        var firstCandidateSegment = segments[firstCandidateIndex];
                        if (firstCandidateSegment.Length == 0 || Path.IsPathRooted(firstCandidateSegment))
                        {
                            candidate = originalPath;
                        }

                        result[originalPath] = candidate;
                    }
                }
            }
        }

        return result;
    }
}
