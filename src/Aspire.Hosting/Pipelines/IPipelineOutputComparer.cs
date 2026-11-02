// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Compares pipeline output snapshots without recapturing live output.
/// </summary>
/// <typeparam name="TSnapshot">The captured representation shared with the matching snapshotter.</typeparam>
/// <remarks>
/// Comparison uses the captured data and does not change either snapshot. Comparing the same snapshots again
/// produces the same changes, independently of subsequent output generation.
/// </remarks>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineOutputComparer<TSnapshot> where TSnapshot : notnull
{
    /// <summary>
    /// Compares output captured before and after artifact generation.
    /// </summary>
    /// <param name="before">The snapshot captured before generation.</param>
    /// <param name="after">The snapshot captured after generation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The changes with output-directory-relative paths. An empty list passes the output check.</returns>
    Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(
        TSnapshot before, TSnapshot after, CancellationToken cancellationToken);
}
