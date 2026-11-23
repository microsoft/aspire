// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Controls selection and built-in fingerprinting for the default pipeline output differ.
/// </summary>
/// <remarks>
/// Configured through the <c>Pipeline:OutputDiff</c> configuration section or ordinary options registration.
/// The comparison target remains opt-in through <see cref="PipelineOutputDiffingExtensions.WithOutputDiffing(IDistributedApplicationBuilder, string, string?, string?)"/>.
/// </remarks>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineOutputDiffOptions
{
    /// <summary>
    /// Gets or sets the membership and fingerprinting strategy. Defaults to <see cref="PipelineOutputDiffMode.Auto"/>.
    /// </summary>
    public PipelineOutputDiffMode Mode { get; set; }

    /// <summary>
    /// Gets the ordered include and exclude glob rules, relative to the global output directory.
    /// </summary>
    /// <remarks>
    /// An empty collection selects all files with <c>Include **/*</c>. A nonempty collection replaces that default:
    /// only included paths are selected, and the last matching rule wins. To exclude from a broad scope, first add
    /// <c>Include **/*</c>, then the exclusions. Git membership filtering applies after these rules when
    /// <see cref="Mode"/> selects Git; an include cannot restore an ignored, untracked file.
    /// When Git is not used, these rules also control selection of Git metadata. Rules affect comparison only, not generation.
    /// The policy is compiled from validated options
    /// when the default differ is resolved; membership is reevaluated at each capture.
    /// </remarks>
    public IList<PipelineOutputDiffRule> Rules { get; } = [];
}

/// <summary>
/// Includes or excludes matching files from pipeline output comparison.
/// </summary>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineOutputDiffRule
{
    /// <summary>
    /// Gets or sets the action, <c>Include</c> or <c>Exclude</c>, matched without regard to case.
    /// </summary>
    public string Action { get; set; } = "";

    /// <summary>
    /// Gets or sets a nonempty relative glob pattern, such as <c>src/**</c> or <c>**/*.tmp</c>.
    /// Patterns cannot be rooted or contain parent-directory segments.
    /// </summary>
    public string Pattern { get; set; } = "";
}
