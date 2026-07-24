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
    /// Gets the root directory of the Git repository containing the specified directory.
    /// </summary>
    /// <param name="startDirectory">The directory from which repository discovery starts.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The root directory of the Git repository, or null if no repository can be resolved.</returns>
    Task<DirectoryInfo?> GetRootAsync(DirectoryInfo startDirectory, CancellationToken cancellationToken);

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

    /// <summary>
    /// Gets tracked files plus untracked, non-ignored files under all specified paths.
    /// </summary>
    /// <param name="repositoryRoot">The explicit Git repository root.</param>
    /// <param name="searchPaths">Absolute file or directory paths to include in the batched query.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A set of absolute included paths, or <c>null</c> when Git execution fails.</returns>
    Task<IReadOnlySet<string>?> GetIncludedFilesAsync(
        DirectoryInfo repositoryRoot,
        IReadOnlyList<string> searchPaths,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the specified absent candidate paths that Git would ignore.
    /// </summary>
    /// <param name="repositoryRoot">The explicit Git repository root.</param>
    /// <param name="candidatePaths">Absolute candidate paths to evaluate in one operation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The ignored absolute paths. An empty set means Git successfully determined that no candidates
    /// are ignored; <c>null</c> means Git execution failed.
    /// </returns>
    Task<IReadOnlySet<string>?> GetIgnoredFilesAsync(
        DirectoryInfo repositoryRoot,
        IReadOnlyList<string> candidatePaths,
        CancellationToken cancellationToken);
}
