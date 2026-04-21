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

        var renderables = RenderBlocksToRenderables(ParseMarkdown(markdown));
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

        return RenderBlocksToPlainText(ParseMarkdown(markdown));
    }

    private static MarkdownDocument ParseMarkdown(string markdown)
    {
        var normalizedMarkdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
        return Markdig.Markdown.Parse(normalizedMarkdown, s_markdownPipeline);
    }

    private static List<IRenderable> RenderBlocksToRenderables(ContainerBlock container)
    {
        var renderables = new List<IRenderable>();

        foreach (var block in container)
        {
            var renderable = RenderBlockToRenderable(block);
            if (renderable is not null)
            {
                renderables.Add(renderable);
            }
        }

        return renderables;
    }

    private static IRenderable? RenderBlockToRenderable(Block block) => block switch
    {
        MarkdigTable table => RenderTableToRenderable(table),
        _ => CreateMarkupRenderable(RenderBlockToMarkup(block))
    };

    private static IRenderable? CreateMarkupRenderable(string markup)
    {
        return string.IsNullOrEmpty(markup)
            ? null
            : new Markup(markup);
    }

    private static string RenderBlocksToPlainText(ContainerBlock container)
    {
        var blocks = new List<string>();

        foreach (var block in container)
        {
            var text = RenderBlockToPlainText(block);
            if (!string.IsNullOrEmpty(text))
            {
                blocks.Add(text);
            }
        }

        return string.Join("\n", blocks);
    }

    private static string RenderBlockToPlainText(Block block) => block switch
    {
        ParagraphBlock paragraph => RenderInlinesToPlainText(paragraph.Inline),
        HeadingBlock heading => RenderInlinesToPlainText(heading.Inline),
        QuoteBlock quote => RenderBlocksToPlainText(quote),
        ListBlock list => RenderListToPlainText(list),
        CodeBlock codeBlock => GetRawCodeText(codeBlock).TrimEnd('\r', '\n'),
        MarkdigTable table => RenderTableToPlainText(table),
        LeafBlock leaf when leaf.Inline is not null => RenderInlinesToPlainText(leaf.Inline),
        ContainerBlock container => RenderBlocksToPlainText(container),
        _ => string.Empty
    };

    private static string RenderBlockToMarkup(Block block) => block switch
    {
        ParagraphBlock paragraph => RenderInlinesToMarkup(paragraph.Inline),
        HeadingBlock heading => ApplyHeadingStyle(heading.Level, RenderInlinesToMarkup(heading.Inline)),
        QuoteBlock quote => RenderQuoteToMarkup(quote),
        ListBlock list => RenderListToMarkup(list),
        CodeBlock codeBlock => $"[grey]{GetRawCodeText(codeBlock).TrimEnd('\r', '\n').EscapeMarkup()}[/]",
        MarkdigTable table => RenderTableToPlainText(table).EscapeMarkup(),
        LeafBlock leaf when leaf.Inline is not null => RenderInlinesToMarkup(leaf.Inline),
        ContainerBlock container => RenderBlocksToMarkup(container),
        _ => string.Empty
    };

    private static string RenderQuoteToMarkup(QuoteBlock quote)
    {
        var quoteMarkup = RenderBlocksToMarkup(quote);
        if (string.IsNullOrEmpty(quoteMarkup))
        {
            return "[italic grey][/]";
        }

        var builder = new StringBuilder();
        var lines = quoteMarkup.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append("[italic grey]");
            builder.Append(lines[i]);
            builder.Append("[/]");
        }

        return builder.ToString();
    }

    private static string RenderListToMarkup(ListBlock list)
    {
        var builder = new StringBuilder();
        var index = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            var prefix = list.IsOrdered ? $"{index++}. " : "* ";
            builder.Append(RenderListItem(item, prefix.EscapeMarkup(), prefix.Length, RenderBlockToMarkup));
        }

        return builder.ToString();
    }

    private static string RenderListToPlainText(ListBlock list)
    {
        var builder = new StringBuilder();
        var index = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            var prefix = list.IsOrdered ? $"{index++}. " : "* ";
            builder.Append(RenderListItem(item, prefix, prefix.Length, RenderBlockToPlainText));
        }

        return builder.ToString();
    }

    private static string RenderListItem(ListItemBlock item, string prefix, int continuationIndent, Func<Block, string> blockRenderer)
    {
        var renderedBlocks = new List<(Block Block, string Content)>();

        foreach (var block in item)
        {
            var content = blockRenderer(block);
            if (!string.IsNullOrEmpty(content))
            {
                renderedBlocks.Add((block, content));
            }
        }

        if (renderedBlocks.Count == 0)
        {
            return prefix.TrimEnd();
        }

        var builder = new StringBuilder();
        var continuationPrefix = new string(' ', continuationIndent);

        if (renderedBlocks[0].Block is ParagraphBlock)
        {
            builder.Append(prefix);
            AppendWithHangingIndent(builder, renderedBlocks[0].Content, continuationPrefix);
        }
        else
        {
            builder.Append(prefix.TrimEnd());
            builder.Append('\n');
            AppendIndentedLines(builder, renderedBlocks[0].Content, continuationPrefix);
        }

        for (var i = 1; i < renderedBlocks.Count; i++)
        {
            builder.Append('\n');
            AppendIndentedLines(builder, renderedBlocks[i].Content, continuationPrefix);
        }

        return builder.ToString();
    }

    private static void AppendWithHangingIndent(StringBuilder builder, string content, string continuationPrefix)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n');

        builder.Append(lines[0]);

        for (var i = 1; i < lines.Length; i++)
        {
            builder.Append('\n');

            if (!string.IsNullOrEmpty(lines[i]))
            {
                builder.Append(continuationPrefix);
            }

            builder.Append(lines[i]);
        }
    }

    private static void AppendIndentedLines(StringBuilder builder, string content, string prefix)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            if (!string.IsNullOrEmpty(lines[i]))
            {
                builder.Append(prefix);
            }

            builder.Append(lines[i]);
        }
    }

    private static string RenderInlinesToMarkup(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var current = inline.FirstChild;

        while (current is not null)
        {
            builder.Append(RenderInlineToMarkup(current));
            current = current.NextSibling;
        }

        return builder.ToString();
    }

    private static string RenderInlineToMarkup(Inline inline) => inline switch
    {
        LiteralInline literal => literal.Content.ToString().EscapeMarkup(),
        CodeInline code => $"[grey][bold]{code.Content.EscapeMarkup()}[/][/]",
        LinkInline { IsImage: true } => string.Empty,
        LinkInline link => RenderLinkToMarkup(RenderInlinesToMarkup(link), link.Url),
        AutolinkInline autolink => RenderLinkToMarkup(autolink.Url.EscapeMarkup(), autolink.Url),
        EmphasisInline emphasis => RenderEmphasisToMarkup(emphasis),
        LineBreakInline => "\n",
        ContainerInline container => RenderInlinesToMarkup(container),
        _ => string.Empty
    };

    private static string RenderEmphasisToMarkup(EmphasisInline emphasis)
    {
        var content = RenderInlinesToMarkup(emphasis);
        return emphasis.DelimiterChar switch
        {
            '~' => $"[strikethrough]{content}[/]",
            '*' or '_' when emphasis.DelimiterCount >= 2 => $"[bold]{content}[/]",
            '*' or '_' => $"[italic]{content}[/]",
            _ => content
        };
    }

    private static string RenderLinkToMarkup(string text, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return text;
        }

        var escapedUrl = url.EscapeMarkup();
        var linkText = string.IsNullOrEmpty(text) ? escapedUrl : text;
        return $"[cyan][link={escapedUrl}]{linkText}[/][/]";
    }

    private static string RenderInlinesToPlainText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var current = inline.FirstChild;

        while (current is not null)
        {
            builder.Append(RenderInlineToPlainText(current));
            current = current.NextSibling;
        }

        return builder.ToString();
    }

    private static string RenderInlineToPlainText(Inline inline) => inline switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        LinkInline { IsImage: true } => string.Empty,
        LinkInline link => RenderLinkToPlainText(RenderInlinesToPlainText(link), link.Url),
        AutolinkInline autolink => autolink.Url,
        EmphasisInline emphasis => RenderInlinesToPlainText(emphasis),
        LineBreakInline => "\n",
        ContainerInline container => RenderInlinesToPlainText(container),
        _ => string.Empty
    };

    private static string RenderLinkToPlainText(string text, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return text;
        }

        return string.IsNullOrEmpty(text) || string.Equals(text, url, StringComparison.Ordinal)
            ? url
            : $"{text} ({url})";
    }

    private static string ApplyHeadingStyle(int level, string content)
    {
        return level switch
        {
            1 => $"[bold green]{content}[/]",
            2 => $"[bold blue]{content}[/]",
            3 => $"[bold yellow]{content}[/]",
            _ => $"[bold]{content}[/]"
        };
    }

    private static IRenderable RenderTableToRenderable(MarkdigTable markdownTable)
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
                ? RenderTableCellToMarkup(headerCell)
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
                    var markup = RenderTableCellToMarkup((MarkdigTableCell)row[i]);
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

    private static string RenderTableToPlainText(MarkdigTable markdownTable)
    {
        var rows = markdownTable.OfType<MarkdigTableRow>().ToList();
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var values = rows
            .Select(row => row
                .OfType<MarkdigTableCell>()
                .Select(RenderTableCellToPlainText)
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
            builder.Append(value.PadRight(widths[i]));
            builder.Append(' ');
            builder.Append('|');
        }
    }

    private static void AppendTableSeparator(StringBuilder builder, IReadOnlyList<int> widths, IReadOnlyList<TableColumnDefinition>? definitions)
    {
        builder.Append('|');
        for (var i = 0; i < widths.Count; i++)
        {
            var width = Math.Max(widths[i], 3);
            var separator = definitions is { Count: > 0 } && i < definitions.Count
                ? definitions[i].Alignment switch
                {
                    TableColumnAlign.Left => ":" + new string('-', Math.Max(width - 1, 2)),
                    TableColumnAlign.Center => ":" + new string('-', Math.Max(width - 2, 1)) + ":",
                    TableColumnAlign.Right => new string('-', Math.Max(width - 1, 2)) + ":",
                    _ => new string('-', width)
                }
                : new string('-', width);

            builder.Append(' ');
            builder.Append(separator);
            builder.Append(' ');
            builder.Append('|');
        }
    }

    private static string RenderTableCellToMarkup(MarkdigTableCell cell)
    {
        return RenderBlocksToMarkup(cell);
    }

    private static string RenderTableCellToPlainText(MarkdigTableCell cell)
    {
        var text = RenderBlocksToPlainText(cell);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string RenderBlocksToMarkup(ContainerBlock container)
    {
        var blocks = new List<string>();

        foreach (var block in container)
        {
            var markup = RenderBlockToMarkup(block);
            if (!string.IsNullOrEmpty(markup))
            {
                blocks.Add(markup);
            }
        }

        return string.Join("\n", blocks);
    }

    private static string GetRawCodeText(CodeBlock codeBlock)
    {
        var builder = new StringBuilder();
        var slices = codeBlock.Lines.Lines;
        if (slices is null)
        {
            return string.Empty;
        }

        for (var i = 0; i < slices.Length; i++)
        {
            ref var slice = ref slices[i].Slice;
            if (slice.Text is null)
            {
                break;
            }

            builder.Append(slice.AsSpan());
            builder.AppendLine();
        }

        return builder.ToString();
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

}
