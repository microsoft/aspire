// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a single item in a <see cref="PipelineSummary"/>, consisting of a key, a value,
/// and a flag indicating whether the value contains Markdown formatting.
/// </summary>
/// <param name="key">The key or label for the summary item.</param>
/// <param name="value">The string value for the summary item.</param>
/// <param name="enableMarkdown">Whether the <paramref name="value"/> contains Markdown formatting.</param>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineSummaryItem(string key, string value, bool enableMarkdown)
{
    /// <summary>
    /// Gets the key or label for the summary item (e.g., "Namespace", "URL").
    /// </summary>
    public string Key { get; } = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>
    /// Gets the string value for the summary item.
    /// </summary>
    public string Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>
    /// Gets a value indicating whether <see cref="Value"/> contains Markdown formatting that
    /// should be rendered by the CLI output.
    /// </summary>
    public bool EnableMarkdown { get; } = enableMarkdown;
}
