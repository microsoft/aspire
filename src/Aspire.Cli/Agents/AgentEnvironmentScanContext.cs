// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// The directories searched by agent environment scanners.
/// </summary>
internal sealed record AgentEnvironmentScanContext
{
    /// <summary>
    /// Gets the working directory being scanned.
    /// </summary>
    public required DirectoryInfo WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the root directory of the repository/workspace.
    /// This is typically the git repository root if available, otherwise the working directory.
    /// Scanners should use this as the boundary for searches instead of searching up the directory tree.
    /// </summary>
    public required DirectoryInfo RepositoryRoot { get; init; }

    public DirectoryInfo? FindProjectDirectory(string directoryName, DirectoryInfo homeDirectory)
    {
        var relativePath = Path.GetRelativePath(RepositoryRoot.FullName, WorkingDirectory.FullName);
        // An explicitly selected workspace need not contain the invocation directory.
        var currentDirectory = Path.IsPathRooted(relativePath) ||
            relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                ? RepositoryRoot
                : WorkingDirectory;
        while (currentDirectory is not null)
        {
            // Home-level agent settings do not establish use in this project, and
            // project discovery must not walk above the selected repository root.
            var path = Path.Combine(currentDirectory.FullName, directoryName);
            if (Directory.Exists(path) &&
                !string.Equals(currentDirectory.FullName, homeDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return new DirectoryInfo(path);
            }

            if (Path.GetRelativePath(RepositoryRoot.FullName, currentDirectory.FullName) == ".")
            {
                break;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }
}
