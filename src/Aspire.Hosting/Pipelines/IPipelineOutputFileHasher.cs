// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Computes hashes that define content and metadata equivalence for selected pipeline output entries.
/// </summary>
/// <remarks>
/// Implementations must support concurrent calls and must not share mutable hash buffers between calls.
/// Implementations own opening and disposing any streams used to read the entry.
/// </remarks>
public interface IPipelineOutputFileHasher
{
    /// <summary>
    /// Computes a hash for one selected pipeline output entry.
    /// </summary>
    /// <remarks>
    /// Treat the supplied entry as a single leaf. It is usually a regular file, but it
    /// may be another filesystem entry, such as a link. A link can target a directory,
    /// another non-file entry, or a missing path. Do not enumerate children of the
    /// entry or recursively traverse its link target.
    /// </remarks>
    /// <param name="entry">The selected leaf entry, with its path and metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An awaitable result containing the entry's hash.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<ReadOnlyMemory<byte>> HashAsync(
        FileSystemInfo entry,
        CancellationToken cancellationToken);
}
