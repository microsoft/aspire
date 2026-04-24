// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aspire.Cli.Utils.Markdown;

/// <summary>
/// Converts basic Markdown syntax to Spectre.Console markup for CLI display.
/// </summary>
internal partial class MarkdownToSpectreConverter
{
    private readonly bool _plainTextLinks;

    private MarkdownToSpectreConverter(bool plainTextLinks)
    {
        _plainTextLinks = plainTextLinks;
    }

    private static readonly MarkdownPipeline s_markdownPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        // This parses additional emphasis forms (for example ==mark== and ++inserted++).
        // We currently only style the common bold/italic/strikethrough cases, but this
        // keeps the AST shape ready if we decide to add richer inline formatting later.
        .UseEmphasisExtras()
        .Build();

    private static MarkdownDocument ParseMarkdown(string markdown, out string normalizedMarkdown)
    {
        normalizedMarkdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
        return Markdig.Markdown.Parse(normalizedMarkdown, s_markdownPipeline);
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

    private enum ReferenceLinkState
    {
        VisibleText,
        OpenBracket,
        Label,
        TrailingText,
    }

    private static ReferenceLinkState ProcessReferenceLinkChild(
        StringBuilder builder, Inline child, ReferenceLinkState state, bool appendEscaped)
    {
        if (child is LinkDelimiterInline nestedDelimiter)
        {
            // Nested reference link — process recursively
            var nestedChild = nestedDelimiter.FirstChild;
            var nestedState = ReferenceLinkState.VisibleText;
            while (nestedChild is not null)
            {
                nestedState = ProcessReferenceLinkChild(builder, nestedChild, nestedState, appendEscaped);
                nestedChild = nestedChild.NextSibling;
            }
            return ReferenceLinkState.TrailingText;
        }

        if (child is not LiteralInline literal)
        {
            return state;
        }

        var content = literal.Content.AsSpan();

        switch (state)
        {
            case ReferenceLinkState.VisibleText:
                // First literal: "text]" — emit everything before the trailing ']'
                if (content.Length > 0 && content[^1] == ']')
                {
                    Append(builder, content[..^1], appendEscaped);
                }
                else
                {
                    Append(builder, content, appendEscaped);
                }
                return ReferenceLinkState.OpenBracket;

            case ReferenceLinkState.OpenBracket:
                // This should be "[" — skip it
                return ReferenceLinkState.Label;

            case ReferenceLinkState.Label:
                // This is "id] trailing text" — skip up to and including first ']', emit rest
                var closeBracket = content.IndexOf(']');
                if (closeBracket >= 0 && closeBracket + 1 < content.Length)
                {
                    Append(builder, content[(closeBracket + 1)..], appendEscaped);
                }
                return ReferenceLinkState.TrailingText;

            case ReferenceLinkState.TrailingText:
                Append(builder, content, appendEscaped);
                return ReferenceLinkState.TrailingText;

            default:
                return state;
        }

        static void Append(StringBuilder sb, ReadOnlySpan<char> text, bool escaped)
        {
            if (escaped)
            {
                AppendEscapedMarkup(sb, text);
            }
            else
            {
                sb.Append(text);
            }
        }
    }

    [GeneratedRegex(@"\[((?:[^\[\]]|\[[^\[\]]*\])+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"</?[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
}
