// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides context for a pipeline environment relevance check.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineEnvironmentCheckContext
{
    /// <summary>
    /// Gets the cancellation token for the check operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }
}
