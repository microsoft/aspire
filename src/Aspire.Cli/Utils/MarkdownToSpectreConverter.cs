// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;
using SpectreTable = Spectre.Console.Table;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;

namespace Aspire.Cli.Utils;

/// <summary>
/// Converts basic Markdown syntax to Spectre.Console markup for CLI display.
/// </summary>
internal static partial class MarkdownToSpectreConverter
{
    private static readonly MarkdownPipeline s_markdownPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        // This parses additional emphasis forms (for example ==mark== and ++inserted++).
        // We currently only style the common bold/italic/strikethrough cases, but this
        // keeps the AST shape ready if we decide to add richer inline formatting later.
        .UseEmphasisExtras()
        .Build();

    /// <summary>
    /// Converts markdown text to a Spectre.Console renderable tree for CLI display.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The converted Spectre.Console renderable.</returns>
    public static IRenderable ConvertToRenderable(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Text.Empty;
        }

        var document = ParseMarkdown(markdown, out var normalizedMarkdown);
        var renderables = RenderBlocksToRenderables(document, normalizedMarkdown);
        return renderables.Count switch
        {
            0 => Text.Empty,
            1 => renderables[0],
            _ => new Rows(renderables)
        };
    }

    /// <summary>
    /// Converts markdown text to Spectre.Console markup.
    /// Supports basic markdown elements: headers, bold, italic, links, and inline code.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The converted Spectre.Console markup text.</returns>
    public static string ConvertToSpectre(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var result = markdown;

        // Normalize line endings to LF to ensure consistent output
        result = result.Replace("\r\n", "\n").Replace("\r", "\n");

        // Process quoted text (> text) - do this first as it's line-based
        result = ConvertQuotedText(result);

        // Process multi-line code blocks (```) - do this before inline code
        result = ConvertCodeBlocks(result);

        // Process headers (# ## ### #### ##### ######)
        result = ConvertHeaders(result);

        // Process bold text (**bold** or __bold__)
        result = ConvertBold(result);

        // Process italic text (*italic* or _italic_)
        result = ConvertItalic(result);

        // Process strikethrough text (~~text~~)
        result = ConvertStrikethrough(result);

        // Process inline code (`code`)
        result = ConvertInlineCode(result);

        // Process images ![alt](url) - remove them as they can't be displayed in CLI
        result = ConvertImages(result);

        // Process links [text](url)
        result = ConvertLinks(result);

        // Escape any remaining square brackets that could be interpreted as Spectre markup
        result = EscapeRemainingSquareBrackets(result);

        return result;
    }

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
    /// <returns>Plain text with links rewritten to <c>text (url)</c>, image references removed, header markers stripped, and basic formatting markers for bold, italic, and strikethrough removed.</returns>
    public static string ConvertToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var document = ParseMarkdown(markdown, out var normalizedMarkdown);
        return RenderBlocksToPlainText(document, normalizedMarkdown);
    }

    private static MarkdownDocument ParseMarkdown(string markdown, out string normalizedMarkdown)
    {
        normalizedMarkdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
        return Markdig.Markdown.Parse(normalizedMarkdown, s_markdownPipeline);
    }

    private static List<IRenderable> RenderBlocksToRenderables(ContainerBlock container, string markdown)
    {
        var renderables = new List<IRenderable>();

        foreach (var block in container)
        {
            var renderable = RenderBlockToRenderable(block, markdown);
            if (renderable is not null)
            {
                if (renderables.Count > 0)
                {
                    renderables.Add(Text.Empty);
                }

                renderables.Add(renderable);
            }
        }

        return renderables;
    }

    private static IRenderable? RenderBlockToRenderable(Block block, string markdown)
    {
        return RenderBlockContentToRenderable(block, markdown);
    }

    private static IRenderable? RenderBlockContentToRenderable(Block block, string markdown) => block switch
    {
        ThematicBreakBlock => new Rule(),
        QuoteBlock quote => RenderQuoteToRenderable(quote, markdown),
        ListBlock list => RenderListToRenderable(list, markdown),
        MarkdigTable table => RenderTableToRenderable(table, markdown),
        _ => CreateMarkupRenderable(block, markdown)
    };

    private static IRenderable? CreateMarkupRenderable(Block block, string markdown)
    {
        var builder = new StringBuilder();
        AppendBlockToMarkup(builder, block, markdown);

        return builder.Length == 0
            ? null
            : new Markup(builder.ToString());
    }

    private static IRenderable? RenderContainerContentToRenderable(ContainerBlock container, string markdown)
    {
        var renderables = new List<IRenderable>();
        Block? previousBlock = null;

        foreach (var block in container)
        {
            var renderable = RenderBlockContentToRenderable(block, markdown);
            if (renderable is null)
            {
                continue;
            }

            if (renderables.Count > 0 && ShouldInsertBlankLineBetween(previousBlock, block))
            {
                renderables.Add(Text.Empty);
            }

            renderables.Add(renderable);
            previousBlock = block;
        }

        return renderables.Count switch
        {
            0 => null,
            1 => renderables[0],
            _ => new Rows(renderables)
        };
    }

    private static bool ShouldInsertBlankLineBetween(Block? previous, Block current)
    {
        if (previous is null)
        {
            return false;
        }

        // Keep nested list and quote content tightly coupled to the preceding block.
        return current is not ListBlock and not QuoteBlock;
    }

    private static IRenderable RenderQuoteToRenderable(QuoteBlock quote, string markdown)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.Columns[0].NoWrap = true;
        grid.Columns[0].Padding = new Padding(0);
        grid.Columns[1].Padding = new Padding(0);

        grid.AddRow(
            new Markup("[grey]>[/] "),
            RenderContainerContentToRenderable(quote, markdown) ?? Text.Empty);

        return grid;
    }

    private static IRenderable RenderListToRenderable(ListBlock list, string markdown)
    {
        var items = list.OfType<ListItemBlock>().ToList();
        if (items.Count == 0)
        {
            return Text.Empty;
        }

        var orderedStart = int.TryParse(list.OrderedStart, out var parsedOrderedStart) ? parsedOrderedStart : 1;

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.Columns[0].NoWrap = true;
        grid.Columns[0].Padding = new Padding(0);
        grid.Columns[1].Padding = new Padding(0);

        var index = orderedStart;
        foreach (var item in items)
        {
            var content = RenderContainerContentToRenderable(item, markdown);
            if (content is null)
            {
                continue;
            }

            var marker = list.IsOrdered ? $"{index++}. " : "• ";
            grid.AddRow(new Markup(marker.EscapeMarkup()), content);
        }

        return grid;
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

    private static bool AppendBlockToMarkup(StringBuilder builder, Block block, string markdown)
    {
        var start = builder.Length;

        switch (block)
        {
            case ParagraphBlock paragraph:
                AppendInlinesToMarkup(builder, paragraph.Inline, markdown);
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
            case MarkdigTable table:
                AppendEscapedMarkup(builder, RenderTableToPlainText(table, markdown).AsSpan());
                break;
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

    private static void AppendHeadingToMarkup(StringBuilder builder, int level, ContainerInline? inline, string markdown)
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

    private static void AppendQuoteToMarkup(StringBuilder builder, QuoteBlock quote, string markdown)
    {
        var quoteStart = builder.Length;
        if (!AppendBlocksToMarkup(builder, quote, markdown))
        {
            builder.Append("[italic grey][/]");
            return;
        }

        WrapAppendedLines(builder, quoteStart, "[italic grey]", "[/]");
    }

    private static bool AppendListToMarkup(StringBuilder builder, ListBlock list, string markdown)
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

    private static bool AppendListItemToMarkup(StringBuilder builder, ListItemBlock item, string prefix, int continuationIndent, string markdown)
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

    private static IRenderable RenderTableToRenderable(MarkdigTable markdownTable, string markdown)
    {
        var rows = markdownTable.OfType<MarkdigTableRow>().ToList();
        if (rows.Count == 0)
        {
            return Text.Empty;
        }

        var columnCount = rows.Max(static row => row.Count);
        var headerRow = rows.FirstOrDefault(static row => row.IsHeader);
        var spectreTable = new SpectreTable();

        for (var i = 0; i < columnCount; i++)
        {
            var headerCell = headerRow is not null && i < headerRow.Count
                ? (MarkdigTableCell)headerRow[i]
                : null;

            var headerMarkup = headerCell is not null
                ? RenderTableCellToMarkup(headerCell, markdown)
                : string.Empty;

            var column = new TableColumn(string.IsNullOrEmpty(headerMarkup) ? Text.Empty : new Markup(headerMarkup));

            if (markdownTable.ColumnDefinitions is { Count: > 0 } && i < markdownTable.ColumnDefinitions.Count)
            {
                var alignment = markdownTable.ColumnDefinitions[i].Alignment;
                if (alignment is not null)
                {
                    column.Alignment = alignment switch
                    {
                        TableColumnAlign.Left => Justify.Left,
                        TableColumnAlign.Center => Justify.Center,
                        TableColumnAlign.Right => Justify.Right,
                        _ => column.Alignment
                    };
                }
            }

            spectreTable.AddColumn(column);
        }

        foreach (var row in rows)
        {
            if (row.IsHeader)
            {
                continue;
            }

            var cells = new IRenderable[columnCount];
            for (var i = 0; i < columnCount; i++)
            {
                if (i < row.Count)
                {
                    var markup = RenderTableCellToMarkup((MarkdigTableCell)row[i], markdown);
                    cells[i] = string.IsNullOrEmpty(markup)
                        ? Text.Empty
                        : new Markup(markup);
                }
                else
                {
                    cells[i] = Text.Empty;
                }
            }

            spectreTable.AddRow(cells);
        }

        return spectreTable;
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

    private static void AppendTableRow(StringBuilder builder, IReadOnlyList<string> row, IReadOnlyList<int> widths)
    {
        builder.Append('|');
        for (var i = 0; i < widths.Count; i++)
        {
            var value = i < row.Count ? row[i] : string.Empty;
            builder.Append(' ');
            builder.Append(value);
            builder.Append(' ', widths[i] - value.Length);
            builder.Append(' ');
            builder.Append('|');
        }
    }

    private static void AppendTableSeparator(StringBuilder builder, IReadOnlyList<int> widths, IReadOnlyList<TableColumnDefinition>? definitions)
    {
        builder.Append('|');
        for (var i = 0; i < widths.Count; i++)
        {
            builder.Append(' ');
            AppendTableSeparatorCell(builder, widths[i], definitions is { Count: > 0 } && i < definitions.Count ? definitions[i].Alignment : null);
            builder.Append(' ');
            builder.Append('|');
        }
    }

    private static string RenderTableCellToMarkup(MarkdigTableCell cell, string markdown)
    {
        var builder = new StringBuilder();
        AppendBlocksToMarkup(builder, cell, markdown);
        return builder.ToString();
    }

    private static string RenderTableCellToPlainText(MarkdigTableCell cell, string markdown)
    {
        return CollapseWhitespace(RenderBlocksToPlainText(cell, markdown));
    }

    private static bool AppendBlocksToMarkup(StringBuilder builder, ContainerBlock container, string markdown)
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

    private static ReadOnlySpan<char> GetOriginalMarkdownSpan(SourceSpan span, string markdown)
    {
        return span.Start < 0 || span.End < span.Start || span.End >= markdown.Length
            ? ReadOnlySpan<char>.Empty
            : markdown.AsSpan(span.Start, span.End - span.Start + 1);
    }

    private static string StripHtmlTags(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return string.Empty;
        }

        return HtmlTagRegex().Replace(text.ToString(), string.Empty);
    }

    private static void AppendCodeBlockToPlainText(StringBuilder builder, CodeBlock codeBlock)
    {
        AppendCodeBlockText(builder, codeBlock, escapeMarkup: false);
    }

    private static void AppendCodeBlockToMarkup(StringBuilder builder, CodeBlock codeBlock)
    {
        builder.Append("[grey]");
        AppendCodeBlockText(builder, codeBlock, escapeMarkup: true);
        builder.Append("[/]");
    }

    private static void AppendCodeBlockText(StringBuilder builder, CodeBlock codeBlock, bool escapeMarkup)
    {
        var slices = codeBlock.Lines.Lines;
        if (slices is null)
        {
            return;
        }

        var wroteContent = false;
        for (var i = 0; i < slices.Length; i++)
        {
            ref var slice = ref slices[i].Slice;
            if (slice.Text is null)
            {
                break;
            }

            if (wroteContent)
            {
                builder.Append('\n');
            }

            if (escapeMarkup)
            {
                AppendEscapedMarkup(builder, slice.AsSpan());
            }
            else
            {
                builder.Append(slice.AsSpan());
            }

            wroteContent = true;
        }
    }

    private static void AppendEscapedMarkup(StringBuilder builder, ReadOnlySpan<char> text)
    {
        var start = 0;

        while (start < text.Length)
        {
            var bracketIndex = text[start..].IndexOfAny('[', ']');
            if (bracketIndex < 0)
            {
                builder.Append(text[start..]);
                return;
            }

            bracketIndex += start;

            if (bracketIndex > start)
            {
                builder.Append(text[start..bracketIndex]);
            }

            builder.Append(text[bracketIndex]);
            builder.Append(text[bracketIndex]);
            start = bracketIndex + 1;
        }
    }

    private static void AppendTableSeparatorCell(StringBuilder builder, int width, TableColumnAlign? alignment)
    {
        switch (alignment)
        {
            case TableColumnAlign.Left:
                builder.Append(':');
                builder.Append('-', Math.Max(width - 1, 2));
                break;
            case TableColumnAlign.Center:
                builder.Append(':');
                builder.Append('-', Math.Max(width - 2, 1));
                builder.Append(':');
                break;
            case TableColumnAlign.Right:
                builder.Append('-', Math.Max(width - 1, 2));
                builder.Append(':');
                break;
            default:
                builder.Append('-', width);
                break;
        }
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void WrapAppendedLines(StringBuilder builder, int contentStart, string linePrefix, string lineSuffix)
    {
        if (contentStart >= builder.Length)
        {
            builder.Append(linePrefix);
            builder.Append(lineSuffix);
            return;
        }

        // Walk backwards so each inserted wrapper leaves earlier newline indexes stable.
        for (var index = builder.Length - 1; index >= contentStart; index--)
        {
            if (builder[index] == '\n')
            {
                builder.Insert(index + 1, linePrefix);
                builder.Insert(index, lineSuffix);
            }
        }

        builder.Insert(contentStart, linePrefix);
        builder.Append(lineSuffix);
    }

    private static void ApplyHangingIndent(StringBuilder builder, int contentStart, int continuationIndent)
    {
        if (continuationIndent <= 0)
        {
            return;
        }

        // The first line already follows the list marker; only continuation lines need padding.
        for (var index = contentStart; index < builder.Length; index++)
        {
            if (builder[index] != '\n')
            {
                continue;
            }

            var nextIndex = index + 1;
            if (nextIndex < builder.Length && builder[nextIndex] != '\n')
            {
                builder.Insert(nextIndex, " ", continuationIndent);
                index = nextIndex + continuationIndent - 1;
            }
        }
    }

    private static void IndentAppendedLines(StringBuilder builder, int contentStart, int indentation)
    {
        if (indentation <= 0 || contentStart >= builder.Length)
        {
            return;
        }

        // Nested blocks are rendered first, then indented in place to avoid another temporary buffer.
        if (builder[contentStart] != '\n')
        {
            builder.Insert(contentStart, " ", indentation);
        }

        for (var index = contentStart; index < builder.Length; index++)
        {
            if (builder[index] != '\n')
            {
                continue;
            }

            var nextIndex = index + 1;
            if (nextIndex < builder.Length && builder[nextIndex] != '\n')
            {
                builder.Insert(nextIndex, " ", indentation);
                index = nextIndex + indentation - 1;
            }
        }
    }

    private static void AppendInlinesToMarkup(StringBuilder builder, ContainerInline? inline, string markdown)
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

    private static void AppendInlineToMarkup(StringBuilder builder, Inline inline, string markdown)
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
                builder.Append("[cyan][link=");
                AppendEscapedMarkup(builder, autolink.Url.AsSpan());
                builder.Append(']');
                AppendEscapedMarkup(builder, autolink.Url.AsSpan());
                builder.Append("[/][/]");
                break;
            case EmphasisInline emphasis:
                AppendEmphasisToMarkup(builder, emphasis, markdown);
                break;
            case LineBreakInline:
                builder.Append('\n');
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

    private static void AppendEmphasisToMarkup(StringBuilder builder, EmphasisInline emphasis, string markdown)
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

    private static void AppendLinkToMarkup(StringBuilder builder, LinkInline link, string markdown)
    {
        if (string.IsNullOrWhiteSpace(link.Url))
        {
            AppendInlinesToMarkup(builder, link, markdown);
            return;
        }

        builder.Append("[cyan][link=");
        AppendEscapedMarkup(builder, link.Url.AsSpan());
        builder.Append(']');

        var contentStart = builder.Length;
        AppendInlinesToMarkup(builder, link, markdown);
        if (builder.Length == contentStart)
        {
            AppendEscapedMarkup(builder, link.Url.AsSpan());
        }

        builder.Append("[/][/]");
    }

    private static void AppendImageToMarkup(StringBuilder builder, LinkInline image, string markdown)
    {
        if (string.IsNullOrWhiteSpace(image.Url))
        {
            AppendInlinesToMarkup(builder, image, markdown);
            return;
        }

        builder.Append("[cyan][link=");
        AppendEscapedMarkup(builder, image.Url.AsSpan());
        builder.Append(']');

        var textStart = builder.Length;
        AppendInlinesToMarkup(builder, image, markdown);
        if (builder.Length == textStart)
        {
            AppendEscapedMarkup(builder, image.Url.AsSpan());
        }

        builder.Append("[/][/]");
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
        if (string.IsNullOrWhiteSpace(image.Url))
        {
            AppendInlinesToPlainText(builder, image, markdown);
            return;
        }

        var textStart = builder.Length;
        AppendInlinesToPlainText(builder, image, markdown);

        if (builder.Length == textStart)
        {
            builder.Append(image.Url);
            return;
        }

        builder.Append(" (");
        builder.Append(image.Url);
        builder.Append(')');
    }

    private static bool AppendedTextEquals(StringBuilder builder, int startIndex, string value)
    {
        var remainingOffset = startIndex;
        var compared = 0;
        var valueSpan = value.AsSpan();

        foreach (var chunk in builder.GetChunks())
        {
            var chunkSpan = chunk.Span;
            if (remainingOffset >= chunkSpan.Length)
            {
                remainingOffset -= chunkSpan.Length;
                continue;
            }

            chunkSpan = chunkSpan[remainingOffset..];
            remainingOffset = 0;

            var count = Math.Min(chunkSpan.Length, valueSpan.Length - compared);
            if (!chunkSpan[..count].SequenceEqual(valueSpan.Slice(compared, count)))
            {
                return false;
            }

            compared += count;
            if (compared == valueSpan.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static string ConvertHeaders(string text)
    {
        // Convert ###### Header 6 (most specific first)
        text = HeaderLevel6Regex().Replace(text, "[bold]$1[/]");

        // Convert ##### Header 5
        text = HeaderLevel5Regex().Replace(text, "[bold]$1[/]");

        // Convert #### Header 4
        text = HeaderLevel4Regex().Replace(text, "[bold]$1[/]");

        // Convert ### Header 3
        text = HeaderLevel3Regex().Replace(text, "[bold yellow]$1[/]");

        // Convert ## Header 2
        text = HeaderLevel2Regex().Replace(text, "[bold blue]$1[/]");

        // Convert # Header 1
        text = HeaderLevel1Regex().Replace(text, "[bold green]$1[/]");

        return text;
    }

    private static string ConvertBold(string text)
    {
        // Convert **bold** and __bold__
        text = BoldDoubleAsterisksRegex().Replace(text, "[bold]$1[/]");
        text = BoldDoubleUnderscoresRegex().Replace(text, "[bold]$1[/]");

        return text;
    }

    private static string ConvertItalic(string text)
    {
        // Convert *italic* and _italic_ (but not ** or __)
        text = ItalicSingleAsteriskRegex().Replace(text, "[italic]$1[/]");
        text = ItalicSingleUnderscoreRegex().Replace(text, "[italic]$1[/]");

        return text;
    }

    private static string ConvertStrikethrough(string text)
    {
        // Convert ~~strikethrough~~
        return StrikethroughRegex().Replace(text, "[strikethrough]$1[/]");
    }

    private static string ConvertCodeBlocks(string text)
    {
        // Convert multi-line code blocks ```code```
        // Remove language name from the beginning if present
        return CodeBlockRegex().Replace(text, match =>
        {
            var content = match.Groups[1].Value.Trim();

            // Check if the first line contains a language name (no spaces, common language names)
            var lines = content.Split('\n');
            if (lines.Length > 1)
            {
                var firstLine = lines[0].Trim();
                // If first line looks like a language name (single word, common languages)
                if (!string.IsNullOrEmpty(firstLine) && !firstLine.Contains(' ') && IsLikelyLanguageName(firstLine))
                {
                    // Remove the language line and rejoin
                    var codeContent = string.Join('\n', lines.Skip(1));
                    return $"[grey]{codeContent}[/]";
                }
            }

            return $"[grey]{content}[/]";
        });
    }

    private static bool IsLikelyLanguageName(string text)
    {
        // Common language names that would appear at the start of code blocks
        var commonLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bash", "sh", "shell", "cmd", "powershell", "ps1",
            "javascript", "js", "typescript", "ts", "jsx", "tsx",
            "python", "py", "java", "c", "cpp", "csharp", "cs", "vb",
            "html", "css", "scss", "sass", "less", "xml", "yaml", "yml", "json",
            "sql", "php", "ruby", "rb", "go", "rust", "swift", "kotlin",
            "scala", "clojure", "haskell", "perl", "lua", "r", "matlab",
            "dockerfile", "makefile", "ini", "toml", "properties"
        };

        return commonLanguages.Contains(text);
    }

    private static string ConvertQuotedText(string text)
    {
        // Convert > quoted text - handle all forms: "> text", "> ", and ">"
        // Process line by line to avoid regex matching across line boundaries
        var lines = text.Split('\n');
        var regex = QuotedTextRegex();

        for (int i = 0; i < lines.Length; i++)
        {
            var match = regex.Match(lines[i]);
            if (match.Success)
            {
                var content = match.Groups[1].Value;
                lines[i] = $"[italic grey]{content}[/]";
            }
        }

        return string.Join('\n', lines);
    }

    private static string ConvertInlineCode(string text)
    {
        // Convert `code`
        return InlineCodeRegex().Replace(text, "[grey][bold]$1[/][/]");
    }

    private static string ConvertImages(string text)
    {
        // Remove image references ![alt](url) as they can't be displayed in CLI
        return ImageRegex().Replace(text, "");
    }

    private static string ConvertLinks(string text)
    {
        // Convert [text](url) to Spectre.Console link format with cyan color
        return LinkRegex().Replace(text, "[cyan][link=$2]$1[/][/]");
    }

    private static string EscapeRemainingSquareBrackets(string text)
    {
        // Escape any remaining square brackets that are not part of Spectre markup
        // We need to preserve Spectre markup tags like [bold], [/], [blue underline], etc.
        // but escape markdown constructs like reference links [text][ref]

        // Use a regex to find standalone square brackets that are not Spectre markup
        // Spectre markup pattern: [word] or [word word] or [/]
        // Reference/other markdown pattern: everything else with square brackets

        // First, temporarily replace all Spectre markup with placeholders
        var spectreMarkups = new List<string>();
        var spectrePattern = @"\[(?:/?(?:bold|italic|grey|blue|green|yellow|cyan|underline|strikethrough)\s?)+\]|\[/\]|\[link=[^\]]+\]|\[cyan\s+link=[^\]]+\]";
        var spectreRegex = new Regex(spectrePattern);

        var textWithPlaceholders = spectreRegex.Replace(text, match =>
        {
            var placeholder = $"__SPECTRE_MARKUP_{spectreMarkups.Count}__";
            spectreMarkups.Add(match.Value);
            return placeholder;
        });

        // Now escape remaining square brackets
        textWithPlaceholders = textWithPlaceholders.Replace("[", "[[").Replace("]", "]]");

        // Restore Spectre markup
        for (int i = 0; i < spectreMarkups.Count; i++)
        {
            textWithPlaceholders = textWithPlaceholders.Replace($"__SPECTRE_MARKUP_{i}__", spectreMarkups[i]);
        }

        return textWithPlaceholders;
    }

    [GeneratedRegex(@"^###### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel6Regex();

    [GeneratedRegex(@"^##### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel5Regex();

    [GeneratedRegex(@"^#### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel4Regex();

    [GeneratedRegex(@"^### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel3Regex();

    [GeneratedRegex(@"^## (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel2Regex();

    [GeneratedRegex(@"^# (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel1Regex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldDoubleAsterisksRegex();

    [GeneratedRegex(@"__([^_]+)__")]
    private static partial Regex BoldDoubleUnderscoresRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*\n]+)\*(?!\*)")]
    private static partial Regex ItalicSingleAsteriskRegex();

    [GeneratedRegex(@"(?<!_)_([^_\n]+)_(?!_)")]
    private static partial Regex ItalicSingleUnderscoreRegex();

    [GeneratedRegex(@"~~([^~]+)~~")]
    private static partial Regex StrikethroughRegex();

    [GeneratedRegex(@"```\s*(.*?)\s*```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[((?:[^\[\]]|\[[^\[\]]*\])+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^>\s*(.*)$", RegexOptions.Multiline)]
    private static partial Regex QuotedTextRegex();

    [GeneratedRegex(@"</?[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

}
