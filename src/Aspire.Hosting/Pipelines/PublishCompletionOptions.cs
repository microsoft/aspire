// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Options for completing the publishing process.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PublishCompletionOptions
{
    /// <summary>
    /// Gets or sets the completion message of the publishing process.
    /// </summary>
    public string? CompletionMessage { get; set; }

    /// <summary>
    /// Gets or sets the completion state of the publishing process.
    /// When <see langword="null"/>, the state is automatically aggregated from all steps.
    /// </summary>
    public CompletionState? CompletionState { get; set; }

    /// <summary>
    /// Gets or sets optional pipeline summary information to display after completion.
    /// The list preserves insertion order. Each item carries its own Markdown formatting flag.
    /// </summary>
    public IReadOnlyList<PipelineSummaryItem>? PipelineSummary { get; set; }
}
