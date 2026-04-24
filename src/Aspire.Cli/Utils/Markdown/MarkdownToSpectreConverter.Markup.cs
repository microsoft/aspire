// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;

namespace Aspire.Cli.Utils.Markdown;

internal partial class MarkdownToSpectreConverter
{
    /// <summary>
    /// Converts markdown text to Spectre.Console markup.
    /// Parses the markdown into an AST and converts blocks to Spectre markup strings.
    /// Structural blocks (quotes, lists, tables, thematic breaks) retain their markdown
    /// text layout while inline content is converted to Spectre markup.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The converted Spectre.Console markup text.</returns>
    public static string ConvertToSpectre(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var converter = new MarkdownToSpectreConverter(plainTextLinks: false);
        var document = ParseMarkdown(markdown, out var normalizedMarkdown);
        var builder = new StringBuilder();
        converter.AppendDocumentBlocksToSpectreMarkup(builder, document, normalizedMarkdown);
        return builder.ToString();
    }

    /// <summary>
    /// Walks top-level blocks of a parsed document and emits Spectre markup strings,
    /// preserving blank-line spacing from the original source.
    /// </summary>
    private void AppendDocumentBlocksToSpectreMarkup(StringBuilder builder, MarkdownDocument document, string markdown)
    {
        Block? previousBlock = null;
        var hasContent = false;

        foreach (var block in document)
        {
            var blockStart = builder.Length;

            if (hasContent && previousBlock is not null)
            {
                // Preserve blank lines between blocks based on source span gaps
                var gapStart = previousBlock.Span.End + 1;
                var gapEnd = block.Span.Start;
                if (gapEnd > gapStart)
                {
                    var newlineCount = markdown.AsSpan(gapStart, gapEnd - gapStart).Count('\n');
                    builder.Append('\n', Math.Max(1, newlineCount));
                }
                else
                {
                    builder.Append('\n');
                }
            }

            if (!AppendSpectreBlock(builder, block, markdown))
            {
                builder.Length = blockStart;
                continue;
            }

            hasContent = true;
            previousBlock = block;
        }
    }

    /// <summary>
    /// Dispatches a block to the appropriate Spectre markup string renderer.
    /// Structural blocks (quotes, lists, tables, thematic breaks) keep their markdown
    /// text layout; all other blocks use <see cref="AppendBlockToMarkup"/>.
    /// </summary>
    private bool AppendSpectreBlock(StringBuilder builder, Block block, string markdown)
    {
        var start = builder.Length;

        switch (block)
        {
            case ThematicBreakBlock:
                AppendEscapedMarkup(builder, GetOriginalMarkdownSpan(block.Span, markdown));
                break;
            case QuoteBlock quote:
                AppendSpectreQuote(builder, quote, markdown);
                break;
            case ListBlock list:
                AppendListToMarkup(builder, list, markdown);
                break;
            case MarkdigTable table:
                AppendSpectreTable(builder, table, markdown);
                break;
            default:
                AppendBlockToMarkup(builder, block, markdown);
                break;
        }

        return builder.Length > start;
    }

    /// <summary>
    /// Renders a quote block with <c>&gt; </c> prefixes preserved as markdown text,
    /// while converting inline content to Spectre markup.
    /// </summary>
    private void AppendSpectreQuote(StringBuilder builder, QuoteBlock quote, string markdown)
    {
        var contentBuilder = new StringBuilder();
        var hasChildContent = false;
        Block? prevChild = null;

        foreach (var child in quote)
        {
            var childStart = contentBuilder.Length;

            if (hasChildContent && prevChild is not null)
            {
                // Preserve blank lines between children
                var gapStart = prevChild.Span.End + 1;
                var gapEnd = child.Span.Start;
                if (gapEnd > gapStart)
                {
                    var newlines = markdown.AsSpan(gapStart, gapEnd - gapStart).Count('\n');
                    contentBuilder.Append('\n', Math.Max(1, newlines));
                }
                else
                {
                    contentBuilder.Append('\n');
                }
            }

            if (!AppendBlockToMarkup(contentBuilder, child, markdown))
            {
                contentBuilder.Length = childStart;
                continue;
            }

            hasChildContent = true;
            prevChild = child;
        }

        if (contentBuilder.Length == 0)
        {
            builder.Append("[italic grey]>[/]");
            return;
        }

        // Prefix each output line with "> " and wrap in italic grey
        var first = true;
        foreach (var line in contentBuilder.ToString().Split('\n'))
        {
            if (!first)
            {
                builder.Append('\n');
            }

            builder.Append("[italic grey]> ");
            builder.Append(line);
            builder.Append("[/]");
            first = false;
        }
    }

    /// <summary>
    /// Renders a Markdig table in pipe-delimited markdown format, converting cell
    /// content to Spectre markup while preserving the table text layout.
    /// </summary>
    private void AppendSpectreTable(StringBuilder builder, MarkdigTable markdownTable, string markdown)
    {
        var rows = markdownTable.OfType<MarkdigTableRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        // Render each cell as markup, then derive plain text widths by stripping markup tags
        var markupValues = rows
            .Select(row => row.OfType<MarkdigTableCell>()
                .Select(cell => RenderTableCellToMarkup(cell, markdown)).ToList())
            .ToList();
        var plainValues = markupValues
            .Select(static row => row.Select(static cell => cell.RemoveMarkup()).ToList())
            .ToList();

        var columnCount = markupValues.Max(static row => row.Count);
        var widths = new int[columnCount];

        foreach (var row in plainValues)
        {
            for (var i = 0; i < columnCount; i++)
            {
                var value = i < row.Count ? row[i] : string.Empty;
                widths[i] = Math.Max(widths[i], value.Length);
            }
        }

        for (var i = 0; i < columnCount; i++)
        {
            widths[i] = Math.Max(widths[i], 3);
        }

        var firstRow = true;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (!firstRow)
            {
                builder.Append('\n');
            }

            // Build pipe-delimited row using markup content, padded by plain text width
            builder.Append('|');
            for (var col = 0; col < widths.Length; col++)
            {
                var markup = col < markupValues[rowIndex].Count ? markupValues[rowIndex][col] : string.Empty;
                var plainLen = col < plainValues[rowIndex].Count ? plainValues[rowIndex][col].Length : 0;
                builder.Append(' ');
                builder.Append(markup);
                builder.Append(' ', widths[col] - plainLen);
                builder.Append(' ');
                builder.Append('|');
            }

            if (rows[rowIndex].IsHeader)
            {
                builder.Append('\n');
                AppendTableSeparator(builder, widths, markdownTable.ColumnDefinitions);
            }

            firstRow = false;
        }
    }

    private bool AppendBlockToMarkup(StringBuilder builder, Block block, string markdown)
    {
        var start = builder.Length;

        switch (block)
        {
            case ParagraphBlock paragraph:
                AppendInlinesToMarkup(builder, paragraph.Inline, markdown);
                while (builder.Length > start && builder[builder.Length - 1] == '\n')
                {
                    builder.Length--;
                }
                break;
            case HtmlBlock htmlBlock:
                AppendEscapedMarkup(builder, StripHtmlTags(GetOriginalMarkdownSpan(htmlBlock.Span, markdown)).AsSpan());
                break;
            case HeadingBlock heading:
                AppendHeadingToMarkup(builder, heading.Level, heading.Inline, markdown);
                break;
            case QuoteBlock quote:
                AppendQuoteToMarkup(builder, quote, markdown);
                break;
            case ListBlock list:
                AppendListToMarkup(builder, list, markdown);
                break;
            case CodeBlock codeBlock:
                AppendCodeBlockToMarkup(builder, codeBlock);
                break;
            case MarkdigTable:
                // Table is hard to support. For now let's preserve the original markdown for tables to ensure they remain readable.
                // Improve in the future if it becomes important to support Spectre markup inside tables.
                goto default;
            case LeafBlock leaf when leaf.Inline is not null:
                AppendInlinesToMarkup(builder, leaf.Inline, markdown);
                break;
            case ContainerBlock container:
                AppendBlocksToMarkup(builder, container, markdown);
                break;
            default:
                // Keep unsupported block nodes visible in interactive output too. Escaping
                // preserves the literal markdown without letting Spectre treat it as markup.
                AppendEscapedMarkup(builder, GetOriginalMarkdownSpan(block.Span, markdown));
                break;
        }

        return builder.Length > start;
    }

    private bool AppendBlocksToMarkup(StringBuilder builder, ContainerBlock container, string markdown)
    {
        var hasContent = false;

        foreach (var block in container)
        {
            var blockStart = builder.Length;
            if (hasContent)
            {
                builder.Append('\n');
            }

            if (!AppendBlockToMarkup(builder, block, markdown))
            {
                builder.Length = blockStart;
                continue;
            }

            hasContent = true;
        }

        return hasContent;
    }

    private void AppendHeadingToMarkup(StringBuilder builder, int level, ContainerInline? inline, string markdown)
    {
        builder.Append(level switch
        {
            1 => "[bold green]",
            2 => "[bold blue]",
            3 => "[bold yellow]",
            _ => "[bold]"
        });

        AppendInlinesToMarkup(builder, inline, markdown);
        builder.Append("[/]");
    }

    private void AppendQuoteToMarkup(StringBuilder builder, QuoteBlock quote, string markdown)
    {
        var quoteStart = builder.Length;
        if (!AppendBlocksToMarkup(builder, quote, markdown))
        {
            builder.Append("[italic grey][/]");
            return;
        }

        WrapAppendedLines(builder, quoteStart, "[italic grey]", "[/]");
    }

    private bool AppendListToMarkup(StringBuilder builder, ListBlock list, string markdown)
    {
        var start = builder.Length;
        var hasContent = false;
        var index = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var itemStart = builder.Length;
            if (hasContent)
            {
                builder.Append('\n');
            }

            var prefix = list.IsOrdered ? $"{index++}. " : "* ";
            if (!AppendListItemToMarkup(builder, item, prefix, prefix.Length, markdown))
            {
                builder.Length = itemStart;
                continue;
            }

            hasContent = true;
        }

        return builder.Length > start;
    }

    private bool AppendListItemToMarkup(StringBuilder builder, ListItemBlock item, string prefix, int continuationIndent, string markdown)
    {
        var start = builder.Length;
        var hasContent = false;
        var trimmedPrefix = prefix.TrimEnd();

        foreach (var block in item)
        {
            var blockStart = builder.Length;

            if (!hasContent)
            {
                if (block is ParagraphBlock)
                {
                    builder.Append(prefix);
                    var contentStart = builder.Length;
                    if (!AppendBlockToMarkup(builder, block, markdown))
                    {
                        builder.Length = blockStart;
                        continue;
                    }

                    ApplyHangingIndent(builder, contentStart, continuationIndent);
                }
                else
                {
                    builder.Append(trimmedPrefix);
                    builder.Append('\n');
                    var contentStart = builder.Length;
                    if (!AppendBlockToMarkup(builder, block, markdown))
                    {
                        builder.Length = blockStart;
                        continue;
                    }

                    IndentAppendedLines(builder, contentStart, continuationIndent);
                }

                hasContent = true;
                continue;
            }

            builder.Append('\n');
            var nestedContentStart = builder.Length;
            if (!AppendBlockToMarkup(builder, block, markdown))
            {
                builder.Length = blockStart;
                continue;
            }

            IndentAppendedLines(builder, nestedContentStart, continuationIndent);
        }

        if (!hasContent)
        {
            builder.Append(trimmedPrefix);
        }

        return builder.Length > start;
    }

    private static void AppendCodeBlockToMarkup(StringBuilder builder, CodeBlock codeBlock)
    {
        builder.Append("[grey]");
        AppendCodeBlockText(builder, codeBlock, escapeMarkup: true);
        builder.Append("[/]");
    }

    private string RenderTableCellToMarkup(MarkdigTableCell cell, string markdown)
    {
        var builder = new StringBuilder();
        AppendBlocksToMarkup(builder, cell, markdown);
        return builder.ToString();
    }

    private void AppendInlinesToMarkup(StringBuilder builder, ContainerInline? inline, string markdown)
    {
        if (inline is null)
        {
            return;
        }

        var current = inline.FirstChild;
        while (current is not null)
        {
            AppendInlineToMarkup(builder, current, markdown);
            current = current.NextSibling;
        }
    }

    private void AppendInlineToMarkup(StringBuilder builder, Inline inline, string markdown)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendEscapedMarkup(builder, literal.Content.AsSpan());
                break;
            case HtmlInline htmlInline:
                AppendEscapedMarkup(builder, StripHtmlTags(GetOriginalMarkdownSpan(htmlInline.Span, markdown)).AsSpan());
                break;
            case CodeInline code:
                builder.Append("[grey][bold]");
                AppendEscapedMarkup(builder, code.Content.AsSpan());
                builder.Append("[/][/]");
                break;
            case LinkInline link:
                if (link.IsImage)
                {
                    AppendImageToMarkup(builder, link, markdown);
                }
                else
                {
                    AppendLinkToMarkup(builder, link, markdown);
                }
                break;
            case AutolinkInline autolink:
                if (_plainTextLinks)
                {
                    AppendEscapedMarkup(builder, autolink.Url.AsSpan());
                }
                else
                {
                    builder.Append("[cyan][link=");
                    AppendEscapedMarkup(builder, autolink.Url.AsSpan());
                    builder.Append(']');
                    AppendEscapedMarkup(builder, autolink.Url.AsSpan());
                    builder.Append("[/][/]");
                }
                break;
            case EmphasisInline emphasis:
                AppendEmphasisToMarkup(builder, emphasis, markdown);
                break;
            case LineBreakInline:
                builder.Append('\n');
                break;
            case LinkDelimiterInline linkDelimiter:
                // Unresolved reference-style links like [text][id] leave a LinkDelimiterInline
                // whose children are literals like "text]", "[", "id]...". Emit only the visible
                // text (the first child's content minus the trailing ']').
                AppendUnresolvedLinkDelimiterToMarkup(builder, linkDelimiter);
                break;
            case ContainerInline container:
                AppendInlinesToMarkup(builder, container, markdown);
                break;
            default:
                // Preserve unsupported inline nodes literally so future Markdig constructs
                // remain readable until we add explicit formatting support for them.
                AppendEscapedMarkup(builder, GetOriginalMarkdownSpan(inline.Span, markdown));
                break;
        }
    }

    private void AppendEmphasisToMarkup(StringBuilder builder, EmphasisInline emphasis, string markdown)
    {
        var (startTag, endTag) = emphasis.DelimiterChar switch
        {
            '~' => ("[strikethrough]", "[/]"),
            '*' or '_' when emphasis.DelimiterCount >= 2 => ("[bold]", "[/]"),
            '*' or '_' => ("[italic]", "[/]"),
            _ => (string.Empty, string.Empty)
        };

        // Unmapped emphasis extras currently degrade to plain child text. This is the place
        // to add CLI styling later if we want explicit support for more Markdig extensions.
        if (startTag.Length > 0)
        {
            builder.Append(startTag);
        }

        AppendInlinesToMarkup(builder, emphasis, markdown);

        if (endTag.Length > 0)
        {
            builder.Append(endTag);
        }
    }

    private void AppendLinkToMarkup(StringBuilder builder, LinkInline link, string markdown)
    {
        if (string.IsNullOrWhiteSpace(link.Url))
        {
            // Reference-style links don't have a URL in the inline node. Keep only visible text.
            AppendInlinesToMarkup(builder, link, markdown);
            return;
        }

        if (_plainTextLinks)
        {
            var contentStart = builder.Length;
            AppendInlinesToMarkup(builder, link, markdown);

            var appendedLength = builder.Length - contentStart;
            if (appendedLength == 0 || appendedLength == link.Url.Length && AppendedTextEquals(builder, contentStart, link.Url))
            {
                if (appendedLength == 0)
                {
                    AppendEscapedMarkup(builder, link.Url.AsSpan());
                }

                return;
            }

            builder.Append(" (");
            AppendEscapedMarkup(builder, link.Url.AsSpan());
            builder.Append(')');
            return;
        }

        builder.Append("[cyan][link=");
        AppendEscapedMarkup(builder, link.Url.AsSpan());
        builder.Append(']');

        var linkContentStart = builder.Length;
        AppendInlinesToMarkup(builder, link, markdown);
        if (builder.Length == linkContentStart)
        {
            AppendEscapedMarkup(builder, link.Url.AsSpan());
        }

        builder.Append("[/][/]");
    }

    private void AppendImageToMarkup(StringBuilder builder, LinkInline image, string markdown)
    {
        // When the image is nested inside a link (linked image), emit the alt text
        // so it becomes the clickable link text of the parent link.
        if (image.Parent is LinkInline)
        {
            AppendInlinesToMarkup(builder, image, markdown);
            return;
        }

        // Standalone images are omitted — they can't be displayed in a terminal.
    }

    private static void AppendUnresolvedLinkDelimiterToMarkup(StringBuilder builder, LinkDelimiterInline delimiter)
    {
        // Unresolved reference-style links [text][id] produce a LinkDelimiterInline whose
        // children include the visible text, bracket literals, and reference label.
        // We emit visible text and trailing content, stripping the reference label brackets.
        var child = delimiter.FirstChild;
        var state = ReferenceLinkState.VisibleText;
        while (child is not null)
        {
            state = ProcessReferenceLinkChild(builder, child, state, appendEscaped: true);
            child = child.NextSibling;
        }
    }
}
