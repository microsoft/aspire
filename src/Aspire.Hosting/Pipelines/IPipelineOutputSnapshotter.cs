// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Captures pipeline output as provider-defined snapshots.
/// </summary>
/// <typeparam name="TSnapshot">The captured representation shared with the matching comparer.</typeparam>
/// <remarks>
/// Implementations may replace default filesystem capture entirely.
/// </remarks>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineOutputSnapshotter<TSnapshot> where TSnapshot : notnull
{
    /// <summary>
    /// Captures the output at the supplied directory.
    /// </summary>
    /// <param name="outputDirectory">The output directory resolved for this capture.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A snapshot of the captured output.</returns>
    Task<TSnapshot> CaptureAsync(string outputDirectory, CancellationToken cancellationToken);
}
