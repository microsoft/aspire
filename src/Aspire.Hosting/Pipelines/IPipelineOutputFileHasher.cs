// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Computes hashes that define content and metadata equivalence for selected pipeline output entries.
/// </summary>
internal interface IPipelineOutputFileHasher
{
    /// <summary>
    /// Hashes a single leaf without enumerating children or traversing its link target.
    /// Implementations own their streams and support concurrent calls without sharing mutable hash buffers.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> HashAsync(
        FileSystemInfo entry,
        CancellationToken cancellationToken);
}
