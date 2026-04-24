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

        var document = ParseMarkdown(markdown, out var normalizedMarkdown);
        return RenderBlocksToPlainText(document, normalizedMarkdown);
    }

    private static string RenderBlocksToPlainText(ContainerBlock container, string markdown)
    {
        var builder = new StringBuilder();
        AppendBlocksToPlainText(builder, container, markdown);
        return builder.ToString();
    }

    private static bool AppendBlocksToPlainText(StringBuilder builder, ContainerBlock container, string markdown)
    {
        var hasContent = false;

        foreach (var block in container)
        {
            var blockStart = builder.Length;
            if (hasContent)
            {
                builder.Append('\n');
            }

            if (!AppendBlockToPlainText(builder, block, markdown))
            {
                builder.Length = blockStart;
                continue;
            }

            hasContent = true;
        }

        return hasContent;
    }

    private static bool AppendBlockToPlainText(StringBuilder builder, Block block, string markdown)
    {
        var start = builder.Length;

        switch (block)
        {
            case ParagraphBlock paragraph:
                AppendInlinesToPlainText(builder, paragraph.Inline, markdown);
                break;
            case HeadingBlock heading:
                AppendInlinesToPlainText(builder, heading.Inline, markdown);
                break;
            case HtmlBlock htmlBlock:
                builder.Append(StripHtmlTags(GetOriginalMarkdownSpan(htmlBlock.Span, markdown)));
                break;
            case QuoteBlock quote:
                AppendBlocksToPlainText(builder, quote, markdown);
                break;
            case ListBlock list:
                AppendListToPlainText(builder, list, markdown);
                break;
            case CodeBlock codeBlock:
                AppendCodeBlockToPlainText(builder, codeBlock);
                break;
            case MarkdigTable table:
                builder.Append(RenderTableToPlainText(table, markdown));
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                AppendInlinesToPlainText(builder, leaf.Inline, markdown);
                break;
            case ContainerBlock container:
                AppendBlocksToPlainText(builder, container, markdown);
                break;
            default:
                // Fail open for block types we do not render yet so unsupported markdown
                // stays visible instead of silently disappearing from the CLI output.
                builder.Append(GetOriginalMarkdownSpan(block.Span, markdown));
                break;
        }

        return builder.Length > start;
    }

    private static bool AppendListToPlainText(StringBuilder builder, ListBlock list, string markdown)
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
            if (!AppendListItemToPlainText(builder, item, prefix, prefix.Length, markdown))
            {
                builder.Length = itemStart;
                continue;
            }

            hasContent = true;
        }

        return builder.Length > start;
    }

    private static bool AppendListItemToPlainText(StringBuilder builder, ListItemBlock item, string prefix, int continuationIndent, string markdown)
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
                    if (!AppendBlockToPlainText(builder, block, markdown))
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
                    if (!AppendBlockToPlainText(builder, block, markdown))
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
            if (!AppendBlockToPlainText(builder, block, markdown))
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

    private static void AppendCodeBlockToPlainText(StringBuilder builder, CodeBlock codeBlock)
    {
        AppendCodeBlockText(builder, codeBlock, escapeMarkup: false);
    }

    private static string RenderTableToPlainText(MarkdigTable markdownTable, string markdown)
    {
        var rows = markdownTable.OfType<MarkdigTableRow>().ToList();
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var values = rows
            .Select(row => row
                .OfType<MarkdigTableCell>()
                .Select(cell => RenderTableCellToPlainText(cell, markdown))
                .ToList())
            .ToList();

        var columnCount = values.Max(static row => row.Count);
        var widths = new int[columnCount];

        foreach (var row in values)
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

        var builder = new StringBuilder();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            AppendTableRow(builder, values[rowIndex], widths);

            if (rows[rowIndex].IsHeader)
            {
                builder.Append('\n');
                AppendTableSeparator(builder, widths, markdownTable.ColumnDefinitions);
            }
        }

        return builder.ToString();
    }

    private static string RenderTableCellToPlainText(MarkdigTableCell cell, string markdown)
    {
        return CollapseWhitespace(RenderBlocksToPlainText(cell, markdown));
    }

    private static void AppendInlinesToPlainText(StringBuilder builder, ContainerInline? inline, string markdown)
    {
        if (inline is null)
        {
            return;
        }

        var current = inline.FirstChild;
        while (current is not null)
        {
            AppendInlineToPlainText(builder, current, markdown);
            current = current.NextSibling;
        }
    }

    private static void AppendInlineToPlainText(StringBuilder builder, Inline inline, string markdown)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.AsSpan());
                break;
            case HtmlInline htmlInline:
                builder.Append(StripHtmlTags(GetOriginalMarkdownSpan(htmlInline.Span, markdown)));
                break;
            case CodeInline code:
                builder.Append(code.Content);
                break;
            case LinkInline link:
                if (link.IsImage)
                {
                    AppendImageToPlainText(builder, link, markdown);
                }
                else
                {
                    AppendLinkToPlainText(builder, link, markdown);
                }
                break;
            case AutolinkInline autolink:
                builder.Append(autolink.Url);
                break;
            case EmphasisInline emphasis:
                AppendInlinesToPlainText(builder, emphasis, markdown);
                break;
            case LineBreakInline:
                builder.Append('\n');
                break;
            case LinkDelimiterInline linkDelimiterPlain:
                AppendUnresolvedLinkDelimiterToPlainText(builder, linkDelimiterPlain);
                break;
            case ContainerInline container:
                AppendInlinesToPlainText(builder, container, markdown);
                break;
            default:
                // Plain-text mode also fails open for unsupported inline nodes so callers
                // can still see the original markdown syntax instead of losing content.
                builder.Append(GetOriginalMarkdownSpan(inline.Span, markdown));
                break;
        }
    }

    private static void AppendLinkToPlainText(StringBuilder builder, LinkInline link, string markdown)
    {
        if (string.IsNullOrWhiteSpace(link.Url))
        {
            AppendInlinesToPlainText(builder, link, markdown);
            return;
        }

        var contentStart = builder.Length;
        AppendInlinesToPlainText(builder, link, markdown);

        var appendedLength = builder.Length - contentStart;
        if (appendedLength == 0 || appendedLength == link.Url.Length && AppendedTextEquals(builder, contentStart, link.Url))
        {
            if (appendedLength == 0)
            {
                builder.Append(link.Url);
            }

            return;
        }

        builder.Append(" (");
        builder.Append(link.Url);
        builder.Append(')');
    }

    private static void AppendImageToPlainText(StringBuilder builder, LinkInline image, string markdown)
    {
        // When the image is nested inside a link (linked image), emit the alt text
        // so it becomes the display text of the parent link.
        if (image.Parent is LinkInline)
        {
            AppendInlinesToPlainText(builder, image, markdown);
            return;
        }

        // Standalone images are omitted — they can't be displayed in a terminal.
    }

    private static void AppendUnresolvedLinkDelimiterToPlainText(StringBuilder builder, LinkDelimiterInline delimiter)
    {
        var child = delimiter.FirstChild;
        var state = ReferenceLinkState.VisibleText;
        while (child is not null)
        {
            state = ProcessReferenceLinkChild(builder, child, state, appendEscaped: false);
            child = child.NextSibling;
        }
    }
}
