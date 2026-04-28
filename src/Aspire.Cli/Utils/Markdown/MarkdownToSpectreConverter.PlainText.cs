// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Spectre.Console;

namespace Aspire.Cli.Utils.Markdown;

internal partial class MarkdownToSpectreConverter
{
    /// <summary>
    /// Converts markdown links to plain text.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The text with markdown links converted to the plain text format <c>text (url)</c>.</returns>
    public static string ConvertLinksToPlainText(string markdown)
    {
        return LinkRegex().Replace(markdown, "$1 ($2)");
    }

    /// <summary>
    /// Converts markdown to a lossy plain-text representation suitable for redirected or non-interactive output.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>Plain text with links rewritten to <c>text (url)</c>, styling applied via Spectre renderable tree, and ANSI escape sequences stripped.</returns>
    public static string ConvertToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var renderable = ConvertToRenderable(markdown, plainTextLinks: true);

        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });

        console.Profile.Width = int.MaxValue;
        console.Write(renderable);
        return writer.ToString();
    }
}
