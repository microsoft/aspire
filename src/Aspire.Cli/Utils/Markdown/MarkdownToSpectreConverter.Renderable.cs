// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Spectre.Console;
using Spectre.Console.Rendering;
using SpectreTable = Spectre.Console.Table;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;

namespace Aspire.Cli.Utils.Markdown;

internal partial class MarkdownToSpectreConverter
{
    /// <summary>
    /// Converts markdown text to a Spectre.Console renderable tree for CLI display.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <param name="plainTextLinks">When <c>true</c>, links are rendered as <c>text (url)</c> instead of terminal hyperlinks.</param>
    /// <returns>The converted Spectre.Console renderable.</returns>
    public static IRenderable ConvertToRenderable(string markdown, bool plainTextLinks = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Text.Empty;
        }

        var converter = new MarkdownToSpectreConverter(plainTextLinks);
        var document = ParseMarkdown(markdown, out var normalizedMarkdown);
        var renderables = converter.RenderBlocksToRenderables(document, normalizedMarkdown);
        return renderables.Count switch
        {
            0 => Text.Empty,
            1 => renderables[0],
            _ => new Rows(renderables)
        };
    }

    private List<IRenderable> RenderBlocksToRenderables(ContainerBlock container, string markdown)
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

    private IRenderable? RenderBlockToRenderable(Block block, string markdown)
    {
        return RenderBlockContentToRenderable(block, markdown);
    }

    private IRenderable? RenderBlockContentToRenderable(Block block, string markdown) => block switch
    {
        ThematicBreakBlock => new Markup(GetOriginalMarkdownSpan(block.Span, markdown).ToString().EscapeMarkup()),
        QuoteBlock quote => RenderQuoteToRenderable(quote, markdown),
        ListBlock list => RenderListToRenderable(list, markdown),
        MarkdigTable table => RenderTableToRenderable(table, markdown),
        _ => CreateMarkupRenderable(block, markdown)
    };

    private IRenderable? CreateMarkupRenderable(Block block, string markdown)
    {
        var builder = new StringBuilder();
        AppendBlockToMarkup(builder, block, markdown);

        return builder.Length == 0
            ? null
            : new Markup(builder.ToString());
    }

    private IRenderable? RenderContainerContentToRenderable(ContainerBlock container, string markdown)
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

    private IRenderable RenderQuoteToRenderable(QuoteBlock quote, string markdown)
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

    private IRenderable RenderListToRenderable(ListBlock list, string markdown)
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

    private IRenderable RenderTableToRenderable(MarkdigTable markdownTable, string markdown)
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
}
