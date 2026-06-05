// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a string that contains Markdown-formatted content.
/// </summary>
/// <remarks>
/// Use this type to explicitly indicate that a string value should be interpreted as Markdown
/// when displayed in pipeline output. APIs that accept both <see cref="string"/> and
/// <see cref="MarkdownString"/> will render Markdown formatting (such as bold, links, and lists)
/// only when a <see cref="MarkdownString"/> is provided; plain <see cref="string"/> values are
/// displayed as-is without Markdown processing.
/// </remarks>
/// <example>
/// <code>
/// // Log a message with Markdown formatting
/// step.Log(LogLevel.Information, new MarkdownString("Deployed **myapp** to [endpoint](https://example.com)"));
///
/// // Add a Markdown-formatted value to the pipeline summary
/// summary.Add("Key", new MarkdownString($"[{name}]({url})"));
/// </code>
/// </example>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class MarkdownString
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownString"/> class with the specified Markdown content.
    /// </summary>
    /// <param name="value">The Markdown-formatted string value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public MarkdownString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>
    /// Gets the Markdown-formatted string value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
