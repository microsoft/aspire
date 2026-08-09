// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

/// <summary>
/// Interface for Git repository operations.
/// </summary>
internal interface IGitRepository
{
    /// <summary>
    /// Gets the root directory of the Git repository, if one exists.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The root directory of the Git repository, or null if not in a Git repository or Git is not installed.</returns>
    Task<DirectoryInfo?> GetRootAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the set of files that git considers part of the repository within the specified
    /// search root: tracked files (<c>--cached</c>) plus untracked files that are not ignored
    /// by <c>.gitignore</c>, <c>.git/info/exclude</c>, or the user's global excludes
    /// (<c>--others --exclude-standard</c>). Submodule contents are not enumerated.
    /// </summary>
    /// <param name="searchRoot">The directory to scope the listing to. Files outside this directory are not returned.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A set of absolute paths to the included files, or <c>null</c> when git is not
    /// installed, the directory is not inside a working tree, or the command otherwise fails.
    /// The set may include paths that no longer exist on disk (for example, tracked files that
    /// have been deleted from the working tree); callers should perform their own existence checks.
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="GetRootAsync(CancellationToken)"/>, this method takes an explicit
    /// search root rather than using the CLI execution context. This lets callers scope
    /// discovery to a specific directory (for example, when the user passes <c>--project</c>
    /// pointing at a sub-directory that differs from the current working directory).
    /// </remarks>
    Task<IReadOnlySet<string>?> GetIncludedFilesAsync(DirectoryInfo searchRoot, CancellationToken cancellationToken);
}
