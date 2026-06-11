// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Utils.Markdown;

/// <summary>
/// Converts markdown links to plain text.
/// </summary>
internal static partial class MarkdownLinkConverter
{
    /// <summary>
    /// Converts markdown links to plain text.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The text with markdown links converted to the plain text format <c>text (url)</c>.</returns>
    public static string ConvertLinksToPlainText(string markdown)
    {
        return LinkRegex().Replace(markdown, match =>
        {
            var text = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value;
            return $"{text} ({url})";
        });
    }

    [GeneratedRegex(@"\[((?:[^\[\]]|\[[^\[\]]*\])+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();
}
