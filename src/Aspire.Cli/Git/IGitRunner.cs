// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal interface IGitRunner
{
    /// <summary>
    /// Resolves a branch, tag, or full commit SHA to a SHA. A null reference selects the remote HEAD.
    /// Unqualified branch names take precedence over tags with the same name.
    /// </summary>
    Task<string> ResolveReferenceAsync(string repository, string? reference, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a shallow, detached checkout of the exact commit with independent object files.
    /// Throws <see cref="GitCommandException"/> if Git cannot start or a command fails.
    /// </summary>
    Task CloneAsync(string repository, DirectoryInfo destination, string commitSha, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches an exact commit into a new shallow bare repository without checking out files.
    /// </summary>
    Task FetchAsync(string repository, DirectoryInfo destination, string commitSha, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the blob ID of a regular file at a literal repository-relative path, rejecting symlinks.
    /// Pass the bare repository directory or a checkout's .git directory.
    /// </summary>
    Task<string> GetBlobIdAsync(DirectoryInfo gitDirectory, string commitSha, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the exact blob bytes, rejecting blobs larger than the caller's limit before reading content.
    /// </summary>
    Task<byte[]> ReadBlobAsync(DirectoryInfo gitDirectory, string blobId, int maximumBytes, CancellationToken cancellationToken);
}
