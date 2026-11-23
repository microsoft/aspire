// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Selects membership and fingerprinting for pipeline output comparison.
/// </summary>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum PipelineOutputDiffMode
{
    /// <summary>
    /// Uses <see cref="Git"/> when the executable and a usable worktree are available; otherwise uses <see cref="FileSystem"/>.
    /// </summary>
    Auto,

    /// <summary>
    /// Requires Git membership and canonical fingerprints, failing if Git or a usable worktree is unavailable.
    /// </summary>
    Git,

    /// <summary>
    /// Uses filesystem membership and raw-byte XxHash3, including matching ignored files, without invoking Git.
    /// </summary>
    FileSystem
}
