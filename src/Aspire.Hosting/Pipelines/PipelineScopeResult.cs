// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents the resolved scope for the current pipeline execution context,
/// identifying both the workflow run and the specific job within that run.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineScopeResult
{
    /// <summary>
    /// Gets the unique identifier for the current workflow run.
    /// </summary>
    /// <remarks>
    /// Used to isolate state directories so concurrent workflow runs on the same machine
    /// (e.g., self-hosted CI runners) don't interfere with each other. For GitHub Actions,
    /// this is typically composed from <c>GITHUB_RUN_ID</c> and <c>GITHUB_RUN_ATTEMPT</c>.
    /// </remarks>
    public required string RunId { get; init; }

    /// <summary>
    /// Gets the unique identifier for the current job/scope within the workflow run.
    /// </summary>
    /// <remarks>
    /// Used for step filtering (determining which steps to execute vs. restore) and
    /// state file naming. For GitHub Actions, this comes from <c>GITHUB_JOB</c>.
    /// </remarks>
    public required string JobId { get; init; }
}
