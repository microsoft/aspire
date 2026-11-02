// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Captures and compares pipeline output across artifact generation.
/// </summary>
/// <remarks>
/// Implementations may replace default filesystem capture entirely.
/// </remarks>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineOutputDiffer
{
    /// <summary>
    /// Captures output before artifact generation.
    /// </summary>
    /// <param name="outputDirectory">The output directory resolved for this capture.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing capture completion.</returns>
    Task CaptureBeforeAsync(string outputDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Captures output after artifact generation.
    /// </summary>
    /// <param name="outputDirectory">The output directory resolved for this capture.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing capture completion.</returns>
    Task CaptureAfterAsync(string outputDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Compares the captured output.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The changes with output-directory-relative paths. An empty list passes the output check.</returns>
    Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(CancellationToken cancellationToken);
}
