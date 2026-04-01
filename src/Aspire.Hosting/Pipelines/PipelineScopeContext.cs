// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides context for resolving the current pipeline execution scope.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineScopeContext
{
    /// <summary>
    /// Gets the cancellation token for the scope resolution operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }
}
