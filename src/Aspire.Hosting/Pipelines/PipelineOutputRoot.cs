// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Normalizes output directory paths and defines lexical path comparisons.
/// </summary>
internal static class PipelineOutputRoot
{
    /// <summary>
    /// Resolves an output directory to an absolute path, trimming trailing separators except at filesystem roots.
    /// </summary>
    /// <exception cref="ArgumentException">The directory is blank or invalid.</exception>
    internal static string NormalizeDirectoryPath(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
    }

    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static StringComparison PathComparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Tests whether a path is the directory itself or lies lexically within it.
    /// Both inputs must be normalized absolute paths. Links are not resolved; this is not a filesystem security boundary.
    /// </summary>
    internal static bool IsWithinDirectory(string directory, string path) =>
        string.Equals(directory, path, PathComparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, PathComparison);

}
