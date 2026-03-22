// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Aspire.Cli.Mcp.Docs;

/// <summary>
/// Represents a parsed document from llms.txt format.
/// </summary>
internal sealed class LlmsDocument
{
    /// <summary>
    /// Gets the document title (from H1).
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the document slug (URL-friendly title).
    /// </summary>
    public required string Slug { get; init; }

    /// <summary>
    /// Gets the document summary (from blockquote).
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the full document content (including title and summary).
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets the document sections (H2 and below).
    /// </summary>
    public required IReadOnlyList<LlmsSection> Sections { get; init; }
}

/// <summary>
/// Represents a section within a document.
/// </summary>
internal sealed class LlmsSection
{
    /// <summary>
    /// Gets the section heading text.
    /// </summary>
    public required string Heading { get; init; }

    /// <summary>
    /// Gets the heading level (2 for H2, 3 for H3, etc.).
    /// </summary>
    public required int Level { get; init; }

    /// <summary>
    /// Gets the section content (from heading to next heading of same or higher level).
    /// </summary>
    public required string Content { get; init; }
}

/// <summary>
/// Parser for llms.txt format documentation with parallel document processing.
/// </summary>
/// <remarks>
/// Supports both standard markdown with headings on separate lines and minified
/// content with inline headings. Code blocks are properly excluded from heading detection.
/// </remarks>
internal static partial class LlmsTxtParser
{
    /// <summary>
    /// Parses llms.txt content into a collection of documents using parallel processing.
    /// </summary>
    /// <param name="content">The raw llms.txt content.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that resolves to a list of parsed documents.</returns>
    public static Task<IReadOnlyList<LlmsDocument>> ParseAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult<IReadOnlyList<LlmsDocument>>([]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Find all document boundaries (line indices where H1 headers start)
        var docBoundaries = FindDocumentBoundaries(content);
        if (docBoundaries.Count is 0)
        {
            return Task.FromResult<IReadOnlyList<LlmsDocument>>([]);
        }

        var documents = new List<LlmsDocument>(docBoundaries.Count);

        for (var i = 0; i < docBoundaries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startIndex = docBoundaries[i];
            var endIndex = i + 1 < docBoundaries.Count
                ? docBoundaries[i + 1]
                : content.Length;

            var docContent = content.AsMemory(startIndex, endIndex - startIndex);
            var document = ParseDocument(docContent.Span);

            if (document is not null)
            {
                documents.Add(document);
            }
        }

        return Task.FromResult<IReadOnlyList<LlmsDocument>>(documents);
    }

    /// <summary>
    /// Finds the character indices where each H1 header starts.
    /// </summary>
    private static List<int> FindDocumentBoundaries(string content)
    {
        var boundaries = new List<int>();
        var span = content.AsSpan();
        var position = 0;

        // Check if content starts with H1
        if (IsH1Start(span))
        {
            boundaries.Add(0);
        }

        // Find all newline + H1 patterns
        while (position < span.Length)
        {
            var newlineIndex = span[position..].IndexOf('\n');
            if (newlineIndex < 0)
            {
                break;
            }

            position += newlineIndex + 1;

            if (position < span.Length && IsH1Start(span[position..]))
            {
                boundaries.Add(position);
            }
        }

        return boundaries;
    }

    /// <summary>
    /// Checks if the span starts with an H1 header.
    /// </summary>
    private static bool IsH1Start(ReadOnlySpan<char> span)
    {
        // Skip leading whitespace
        var trimmed = span.TrimStart();

        // Must start with "# " (single # followed by space)
        if (trimmed.Length < 2)
        {
            return false;
        }

        return trimmed[0] is '#'
            && trimmed[1] is not '#'
            && (trimmed[1] is ' ' || trimmed.Length is 1);
    }

    /// <summary>
    /// Parses a single document from a content span.
    /// </summary>
    private static LlmsDocument? ParseDocument(ReadOnlySpan<char> docSpan)
    {
        if (docSpan.IsEmpty)
        {
            return null;
        }

        // Find the first line (H1 title)
        var firstNewline = docSpan.IndexOf('\n');
        var titleLine = firstNewline >= 0 ? docSpan[..firstNewline] : docSpan;

        // Extract title text (remove leading #)
        var title = ExtractHeadingText(titleLine);
        if (title.Length is 0)
        {
            return null;
        }

        var titleString = title.ToString();

        // Find summary (first blockquote after title)
        var remaining = firstNewline >= 0 ? docSpan[(firstNewline + 1)..] : [];
        var summary = FindSummary(remaining);

        // Parse sections
        var sections = ParseSections(docSpan);

        // Content is the full span as string
        var content = docSpan.ToString();

        return new LlmsDocument
        {
            Title = titleString,
            Slug = GenerateSlug(titleString),
            Summary = summary,
            Content = content,
            Sections = sections
        };
    }

    /// <summary>
    /// Extracts the heading text (removes leading # characters and whitespace).
    /// </summary>
    private static ReadOnlySpan<char> ExtractHeadingText(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();

        // Skip # characters
        var hashCount = 0;
        while (hashCount < trimmed.Length && trimmed[hashCount] is '#')
        {
            hashCount++;
        }

        if (hashCount is 0)
        {
            return [];
        }

        // Skip space after #s
        var textStart = hashCount;
        if (textStart < trimmed.Length && trimmed[textStart] is ' ')
        {
            textStart++;
        }

        return trimmed[textStart..].Trim();
    }

    /// <summary>
    /// Finds the first blockquote summary in the content.
    /// </summary>
    private static string? FindSummary(ReadOnlySpan<char> content)
    {
        var position = 0;

        while (position < content.Length)
        {
            // Find start of line (skip whitespace)
            var lineStart = position;
            while (lineStart < content.Length && content[lineStart] is ' ' or '\t')
            {
                lineStart++;
            }

            // Check for blockquote
            if (lineStart < content.Length && content[lineStart] is '>')
            {
                // Find end of line
                var lineEnd = content[lineStart..].IndexOf('\n');
                var quoteLine = lineEnd >= 0
                    ? content[lineStart..(lineStart + lineEnd)]
                    : content[lineStart..];

                // Extract text after >
                var quoteText = quoteLine[1..].Trim();
                if (quoteText.Length > 0)
                {
                    return quoteText.ToString();
                }
            }

            // Move to next line
            var nextNewline = content[position..].IndexOf('\n');
            if (nextNewline < 0)
            {
                break;
            }

            position += nextNewline + 1;

            // Stop if we hit a heading (sections start)
            if (position < content.Length && content[position] is '#')
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses H2+ sections from a document span, supporting both newline-delimited
    /// and inline heading formats. Properly excludes code blocks.
    /// </summary>
    private static List<LlmsSection> ParseSections(ReadOnlySpan<char> docSpan)
    {
        var sections = new List<LlmsSection>();

        // Find code block regions to exclude
        var codeBlocks = FindCodeBlockRegions(docSpan);

        // Find all section headings (H2+)
        var sectionStarts = FindSectionHeadings(docSpan, codeBlocks);

        // Build sections with content
        for (var i = 0; i < sectionStarts.Count; i++)
        {
            var (startIndex, level, heading) = sectionStarts[i];

            // Find end of this section (next heading of same or higher level)
            var endIndex = docSpan.Length;
            for (var j = i + 1; j < sectionStarts.Count; j++)
            {
                if (sectionStarts[j].Level <= level)
                {
                    endIndex = sectionStarts[j].Index;
                    break;
                }
            }

            var sectionContent = docSpan[startIndex..endIndex].ToString();

            sections.Add(new LlmsSection
            {
                Heading = heading,
                Level = level,
                Content = sectionContent
            });
        }

        return sections;
    }

    /// <summary>
    /// Finds all code block regions (```...```) to exclude from heading detection.
    /// </summary>
    private static List<(int Start, int End)> FindCodeBlockRegions(ReadOnlySpan<char> content)
    {
        var regions = new List<(int Start, int End)>();
        var position = 0;

        while (position < content.Length - 2)
        {
            // Find opening ```
            var openIndex = content[position..].IndexOf("```");
            if (openIndex < 0)
            {
                break;
            }

            var absoluteOpen = position + openIndex;

            // Find closing ``` (must be after opening)
            var searchStart = absoluteOpen + 3;
            if (searchStart >= content.Length)
            {
                break;
            }

            var closeIndex = content[searchStart..].IndexOf("```");
            if (closeIndex < 0)
            {
                // Unclosed code block - treat rest as code
                regions.Add((absoluteOpen, content.Length));
                break;
            }

            var absoluteClose = searchStart + closeIndex + 3;
            regions.Add((absoluteOpen, absoluteClose));
            position = absoluteClose;
        }

        return regions;
    }

    /// <summary>
    /// Checks if a position is inside any code block region.
    /// </summary>
    private static bool IsInsideCodeBlock(int position, List<(int Start, int End)> codeBlocks)
    {
        foreach (var (start, end) in codeBlocks)
        {
            if (position >= start && position < end)
            {
                return true;
            }

            // Code blocks are sorted, so if we're past this one, check next
            if (position >= end)
            {
                continue;
            }

            // We're before this code block, and all remaining are after
            break;
        }

        return false;
    }

    /// <summary>
    /// Finds all H2+ section headings in the content, excluding code blocks.
    /// Supports both newline-delimited and inline heading formats.
    /// </summary>
    private static List<(int Index, int Level, string Heading)> FindSectionHeadings(
        ReadOnlySpan<char> docSpan,
        List<(int Start, int End)> codeBlocks)
    {
        var sectionStarts = new List<(int Index, int Level, string Heading)>();

        // Skip first line (H1 title)
        var position = docSpan.IndexOf('\n');
        if (position < 0)
        {
            // Single line document - check for inline sections
            position = 0;
            var firstH1End = FindHeadingEnd(docSpan, 0);
            if (firstH1End > 0)
            {
                position = firstH1End;
            }
        }
        else
        {
            position++; // Move past newline
        }

        while (position < docSpan.Length)
        {
            // Skip if inside code block
            if (IsInsideCodeBlock(position, codeBlocks))
            {
                // Jump to end of this code block
                foreach (var (start, end) in codeBlocks)
                {
                    if (position >= start && position < end)
                    {
                        position = end;
                        break;
                    }
                }

                continue;
            }

            // Check for heading at current position
            var headingInfo = TryParseHeading(docSpan, position);
            if (headingInfo.HasValue)
            {
                var (level, headingText, headingEnd) = headingInfo.Value;

                // Only include H2 and below (level >= 2)
                if (level >= 2)
                {
                    sectionStarts.Add((position, level, headingText));
                }

                position = headingEnd;
                continue;
            }

            // Move to next potential heading position
            position = FindNextPotentialHeading(docSpan, position);
            if (position < 0)
            {
                break;
            }
        }

        return sectionStarts;
    }

    /// <summary>
    /// Tries to parse a heading at the given position.
    /// Returns (level, heading text, end position) if found.
    /// </summary>
    private static (int Level, string Heading, int End)? TryParseHeading(ReadOnlySpan<char> content, int position)
    {
        var remaining = content[position..];

        // Check for # at start (possibly after whitespace for newline-based)
        var whitespaceSkipped = 0;
        while (whitespaceSkipped < remaining.Length && remaining[whitespaceSkipped] is ' ' or '\t')
        {
            whitespaceSkipped++;
        }

        var trimmed = remaining[whitespaceSkipped..];

        if (trimmed.IsEmpty || trimmed[0] is not '#')
        {
            return null;
        }

        // Count # characters
        var level = 0;
        while (level < trimmed.Length && trimmed[level] is '#')
        {
            level++;
        }

        // Must have space after #s
        if (level >= trimmed.Length || trimmed[level] is not ' ')
        {
            return null;
        }

        // Extract heading text
        var textStart = level + 1;
        var headingSpan = trimmed[textStart..];

        // Find end of heading - either newline, next heading marker, or [Section titled...]
        var headingEnd = FindHeadingTextEnd(headingSpan);
        var headingText = headingSpan[..headingEnd].Trim().ToString();

        if (string.IsNullOrEmpty(headingText))
        {
            return null;
        }

        // Calculate absolute end position
        var absoluteEnd = position + whitespaceSkipped + textStart + headingEnd;

        // Skip past [Section titled...] marker if present
        var afterHeading = content[absoluteEnd..];
        if (afterHeading.StartsWith("[Section titled"))
        {
            var bracketEnd = afterHeading.IndexOf(']');
            if (bracketEnd >= 0)
            {
                absoluteEnd += bracketEnd + 1;
            }
        }

        return (level, headingText, absoluteEnd);
    }

    /// <summary>
    /// Finds the end of heading text (before newline, next inline heading, or section marker).
    /// </summary>
    private static int FindHeadingTextEnd(ReadOnlySpan<char> headingSpan)
    {
        // Look for end markers
        var newlineIndex = headingSpan.IndexOf('\n');
        var sectionMarkerIndex = headingSpan.IndexOf("[Section titled");
        var nextInlineHeading = FindNextInlineHeadingMarker(headingSpan);

        var end = headingSpan.Length;

        if (newlineIndex >= 0 && newlineIndex < end)
        {
            end = newlineIndex;
        }

        if (sectionMarkerIndex >= 0 && sectionMarkerIndex < end)
        {
            end = sectionMarkerIndex;
        }

        if (nextInlineHeading >= 0 && nextInlineHeading < end)
        {
            end = nextInlineHeading;
        }

        return end;
    }

    /// <summary>
    /// Finds the next inline heading marker (space followed by ##).
    /// </summary>
    private static int FindNextInlineHeadingMarker(ReadOnlySpan<char> span)
    {
        var position = 0;
        while (position < span.Length - 2)
        {
            var spaceIndex = span[position..].IndexOf(" #");
            if (spaceIndex < 0)
            {
                return -1;
            }

            var absoluteIndex = position + spaceIndex;

            // Check if this is a heading (## pattern)
            if (absoluteIndex + 2 < span.Length && span[absoluteIndex + 2] is '#')
            {
                return absoluteIndex;
            }

            position = absoluteIndex + 2;
        }

        return -1;
    }

    /// <summary>
    /// Finds the end of the H1 heading in inline content.
    /// </summary>
    private static int FindHeadingEnd(ReadOnlySpan<char> content, int startPosition)
    {
        var span = content[startPosition..];

        // Look for [Section titled...] marker or next heading
        var sectionMarker = span.IndexOf("[Section titled");
        if (sectionMarker >= 0)
        {
            var bracketEnd = span[sectionMarker..].IndexOf(']');
            if (bracketEnd >= 0)
            {
                return startPosition + sectionMarker + bracketEnd + 1;
            }
        }

        // Look for next heading marker
        var nextHeading = FindNextInlineHeadingMarker(span);
        if (nextHeading >= 0)
        {
            return startPosition + nextHeading;
        }

        return -1;
    }

    /// <summary>
    /// Finds the next position where a heading might start.
    /// </summary>
    private static int FindNextPotentialHeading(ReadOnlySpan<char> content, int currentPosition)
    {
        var remaining = content[currentPosition..];

        // Look for newline (traditional heading)
        var newlineIndex = remaining.IndexOf('\n');

        // Look for inline heading marker ( ##)
        var inlineIndex = FindNextInlineHeadingMarker(remaining);

        // Return whichever comes first
        if (newlineIndex >= 0 && (inlineIndex < 0 || newlineIndex < inlineIndex))
        {
            return currentPosition + newlineIndex + 1;
        }

        if (inlineIndex >= 0)
        {
            return currentPosition + inlineIndex + 1; // +1 to skip the space
        }

        return -1;
    }

    /// <summary>
    /// Generates a URL-friendly slug from a title.
    /// </summary>
    private static string GenerateSlug(string title)
    {
        // Fast path for simple titles
        var span = title.AsSpan();
        var needsProcessing = false;

        foreach (var c in span)
        {
            if (!char.IsLetterOrDigit(c) && c is not ' ' and not '-')
            {
                needsProcessing = true;
                break;
            }

            if (char.IsUpper(c))
            {
                needsProcessing = true;
                break;
            }
        }

        if (!needsProcessing && !span.Contains(' '))
        {
            return title;
        }

        // Use pooled array for building slug
        var buffer = ArrayPool<char>.Shared.Rent(title.Length);

        try
        {
            var writeIndex = 0;
            var lastWasHyphen = true; // Start true to avoid leading hyphens

            foreach (var c in span)
            {
                if (char.IsLetterOrDigit(c))
                {
                    buffer[writeIndex++] = char.ToLowerInvariant(c);
                    lastWasHyphen = false;
                }
                else if ((c is ' ' || c is '-') && !lastWasHyphen)
                {
                    buffer[writeIndex++] = '-';
                    lastWasHyphen = true;
                }
            }

            // Trim trailing hyphen
            if (writeIndex > 0 && buffer[writeIndex - 1] is '-')
            {
                --writeIndex;
            }

            return new string(buffer, 0, writeIndex);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
