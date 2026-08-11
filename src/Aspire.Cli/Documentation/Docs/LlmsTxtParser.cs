// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Aspire.Cli.Documentation.Docs;

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
/// Parser for llms.txt format documentation.
/// </summary>
/// <remarks>
/// <para>
/// The llms.txt convention is defined at <see href="https://llmstxt.org"/>. A
/// concatenated llms.txt corpus is a stream of markdown documents separated by
/// H1 (<c>#</c>) headings. Each document optionally begins with a blockquote
/// "summary" line, followed by H2+ sections.
/// </para>
/// <para>
/// Two physical formats appear in the wild:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Standard markdown — headings on their own line, blank lines
///       between sections. Example:
///       <code>
///       # Document Title
///
///       > One-line summary in a blockquote.
///
///       ## Section One
///
///       Section body.
///
///       ## Section Two
///
///       More body.
///       </code>
///     </description>
///   </item>
///   <item>
///     <description>
///       Minified ("inline") form — newlines collapsed to single spaces
///       by site-generation plugins (notably Starlight's
///       <see href="https://github.com/delucis/starlight-llms-txt">starlight-llms-txt</see>),
///       so the heading marker appears inline with a space prefix
///       (<c>" ## "</c>) and Starlight emits
///       <c>[Section titled "Section One"]</c> as an anchor stub directly
///       after each heading. Example raw line:
///       <code>
///       # Document Title [Section titled Document Title] Body text ## Section One [Section titled Section One] Body of section one. ## Section Two ...
///       </code>
///       Both formats can appear within the same corpus (and even the same
///       document).
///     </description>
///   </item>
/// </list>
/// <para>
/// Fenced code blocks are detected up front and treated as no-fly zones for all
/// heading detection so bash <c>#</c> comments and shell prompts don't get
/// parsed as document or section boundaries.
/// </para>
/// </remarks>
internal static partial class LlmsTxtParser
{
    private const int MaxDocumentTitleLength = 200;

    /// <summary>
    /// Parses llms.txt content into a collection of documents.
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

        // Compute fenced code block regions once over the full content. The regions are
        // used by FindDocumentBoundaries (so bash `#` comments inside fences are not
        // mistaken for H1s) and again by ParseSections (so `##`/`###` inside fences are
        // not mistaken for section headings). Doing it once avoids re-scanning every
        // document's body for ``` runs.
        var span = content.AsSpan();
        var codeBlocks = FindCodeBlockRegions(span);

        // Find all document boundaries (line indices where H1 headers start)
        var docBoundaries = FindDocumentBoundaries(span, codeBlocks);
        if (docBoundaries.Count is 0)
        {
            return Task.FromResult<IReadOnlyList<LlmsDocument>>([]);
        }

        var documents = new List<LlmsDocument>(docBoundaries.Count);
        var slugCounts = new Dictionary<string, int>(docBoundaries.Count, StringComparer.Ordinal);

        for (var i = 0; i < docBoundaries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startIndex = docBoundaries[i];
            var endIndex = i + 1 < docBoundaries.Count
                ? docBoundaries[i + 1]
                : content.Length;

            // Slice the globally-computed fence regions down to this document's window
            // and rebase to document-local indices so ParseSections can treat them the
            // same way it does today. Document boundaries are guaranteed to lie OUTSIDE
            // fences (we skipped inside-fence H1s above), so no fence spans a boundary.
            var docCodeBlocks = SliceCodeBlocks(codeBlocks, startIndex, endIndex);

            var docContent = content.AsMemory(startIndex, endIndex - startIndex);
            var document = ParseDocument(docContent.Span, docCodeBlocks, slugCounts);

            if (document is not null)
            {
                documents.Add(document);
            }
        }

        return Task.FromResult<IReadOnlyList<LlmsDocument>>(documents);
    }

    /// <summary>
    /// Slices <paramref name="regions"/> down to the document window
    /// <c>[<paramref name="startIndex"/>, <paramref name="endIndex"/>)</c> and rebases each
    /// region's <c>Start</c>/<c>End</c> so they are relative to <paramref name="startIndex"/>.
    /// </summary>
    /// <remarks>
    /// Document boundaries are detected to lie outside fenced code blocks, so no fence
    /// in <paramref name="regions"/> straddles a boundary; every overlapping region is
    /// fully contained. Returns a shared empty list when the document has no fences.
    /// </remarks>
    private static (int Start, int End)[] SliceCodeBlocks(
        (int Start, int End)[] regions,
        int startIndex,
        int endIndex)
    {
        if (regions.Length is 0)
        {
            return regions;
        }

        List<(int Start, int End)>? sliced = null;

        foreach (var (s, e) in regions)
        {
            if (e <= startIndex)
            {
                continue;
            }

            if (s >= endIndex)
            {
                break;
            }

            sliced ??= [];
            sliced.Add((s - startIndex, e - startIndex));
        }

        return sliced?.ToArray() ?? s_emptyRegions;
    }

    // Sentinel returned by SliceCodeBlocks for fence-free documents. Most docs in
    // the live corpus contain zero fenced blocks, so returning a shared instance
    // avoids hundreds of empty allocations per parse. Using an array (specifically
    // Array.Empty) over a shared List<T> is deliberate: a shared mutable list
    // would be silently corrupted for every caller if any future consumer ever
    // did Add/Clear on the result. An array is fixed-size and can't grow.
    private static readonly (int Start, int End)[] s_emptyRegions = Array.Empty<(int Start, int End)>();

    /// <summary>
    /// Finds the character indices where each H1 header starts.
    /// </summary>
    /// <remarks>
    /// Headings inside fenced code blocks (e.g., bash <c>#</c> comments) are skipped so they
    /// don't get treated as document boundaries.
    /// </remarks>
    private static List<int> FindDocumentBoundaries(ReadOnlySpan<char> span, (int Start, int End)[] codeBlocks)
    {
        var boundaries = new List<int>();
        var position = 0;

        // Check if content starts with H1
        if (IsDocumentBoundary(span) && !IsInsideCodeBlock(0, codeBlocks))
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

            if (position < span.Length
                && !IsInsideCodeBlock(position, codeBlocks)
                && IsDocumentBoundary(span[position..]))
            {
                boundaries.Add(position);
            }
        }

        return boundaries;
    }

    private static bool IsDocumentBoundary(ReadOnlySpan<char> span)
    {
        if (!IsH1Start(span))
        {
            return false;
        }

        var lineEnd = span.IndexOf('\n');
        var titleLine = lineEnd >= 0 ? span[..lineEnd] : span;
        var title = ExtractHeadingText(titleLine);
        if (title.Length is 0 || title.Length > MaxDocumentTitleLength)
        {
            return false;
        }

        // Reject H1-looking lines that are actually pieces of minified inline
        // content rather than real document headings. Examples observed in the
        // live corpus:
        //
        //   "# Document Title ## Section One"
        //     -> minified form where the H1 and its first H2 share a line; the
        //        same physical document, not a new one.
        //   "# Document Title [Section titled Document Title] Body..."
        //     -> Starlight anchor stub emitted right after the heading; this is
        //        body content, not a new document.
        //   "# Whats new in [Aspire 13.3](/whats-new/aspire-13-3)"
        //     -> markdown link inside what looks like a title; in practice this
        //        only appears in body prose, not real top-level H1s.
        return title.IndexOf("## ", StringComparison.Ordinal) < 0
            && title.IndexOf("[Section titled", StringComparison.Ordinal) < 0
            && title.IndexOf("](", StringComparison.Ordinal) < 0;
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
            && trimmed[1] is ' ';
    }

    /// <summary>
    /// Parses a single document from a content span.
    /// </summary>
    /// <param name="docSpan">The span over the document's content (starting at its H1).</param>
    /// <param name="docCodeBlocks">Fenced code-block regions within <paramref name="docSpan"/>,
    /// rebased to document-local indices. Passed through so <see cref="ParseSections"/> does
    /// not have to re-scan <paramref name="docSpan"/> for ``` runs.</param>
    /// <param name="slugCounts">Tracks slugs already issued in this parse, so we can append
    /// a numeric suffix when two documents would otherwise share the same slug. The dictionary
    /// is mutated in place. Pass <see langword="null"/> only in tests where collision handling
    /// is not exercised.</param>
    private static LlmsDocument? ParseDocument(
        ReadOnlySpan<char> docSpan,
        (int Start, int End)[] docCodeBlocks,
        Dictionary<string, int>? slugCounts)
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

        // Parse sections, reusing the pre-computed fence regions for this document.
        var sections = ParseSections(docSpan, docCodeBlocks);

        // Content is the full span as string
        var content = docSpan.ToString();

        return new LlmsDocument
        {
            Title = titleString,
            Slug = GenerateUniqueSlug(titleString, slugCounts),
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
    private static List<LlmsSection> ParseSections(ReadOnlySpan<char> docSpan, (int Start, int End)[] codeBlocks)
    {
        var sections = new List<LlmsSection>();

        // Find all section headings (H2+) using the pre-computed fence regions
        // passed down from ParseAsync.
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
    private static (int Start, int End)[] FindCodeBlockRegions(ReadOnlySpan<char> content)
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
                // Unclosed fence — extend the region to end-of-content. If the
                // corpus is ever truncated mid-fence (download interrupted,
                // upstream regression), this keeps any stray `# `/`## ` lines
                // inside the unterminated block from being mistaken for real
                // document or section boundaries.
                regions.Add((absoluteOpen, content.Length));
                break;
            }

            var absoluteClose = searchStart + closeIndex + 3;
            regions.Add((absoluteOpen, absoluteClose));
            position = absoluteClose;
        }

        // Convert the build-time List<T> into a fixed-size array so the result is
        // not silently mutable; downstream call sites only read via indexer.
        return regions.Count is 0 ? s_emptyRegions : regions.ToArray();
    }

    /// <summary>
    /// Checks if a position is inside any code block region.
    /// </summary>
    private static bool IsInsideCodeBlock(int position, (int Start, int End)[] codeBlocks)
        => TryGetContainingCodeBlock(position, codeBlocks, out _);

    /// <summary>
    /// If <paramref name="position"/> lies inside one of the (sorted, non-overlapping)
    /// fenced code block regions in <paramref name="codeBlocks"/>, returns <see langword="true"/>
    /// and sets <paramref name="end"/> to that region's exclusive end. Otherwise returns
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Uses binary search over the region list (regions are added in ascending start order
    /// by <c>FindCodeBlockRegions</c>). This is called in two hot loops:
    /// <list type="bullet">
    ///   <item><c>FindDocumentBoundaries</c> tests every newline in the full corpus.</item>
    ///   <item><c>FindSectionHeadings</c> tests every potential heading position and, on a
    ///   hit, must jump to the end of the containing fence. Returning the end here lets that
    ///   callsite skip a second linear walk over the same list.</item>
    /// </list>
    /// </remarks>
    private static bool TryGetContainingCodeBlock(int position, (int Start, int End)[] codeBlocks, out int end)
    {
        var lo = 0;
        var hi = codeBlocks.Length - 1;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var (start, blockEnd) = codeBlocks[mid];

            if (position < start)
            {
                hi = mid - 1;
            }
            else if (position >= blockEnd)
            {
                lo = mid + 1;
            }
            else
            {
                end = blockEnd;
                return true;
            }
        }

        end = -1;
        return false;
    }

    /// <summary>
    /// Finds all H2+ section headings in the content, excluding code blocks.
    /// Supports both newline-delimited and inline heading formats.
    /// </summary>
    private static List<(int Index, int Level, string Heading)> FindSectionHeadings(
        ReadOnlySpan<char> docSpan,
        (int Start, int End)[] codeBlocks)
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
            // Skip if inside code block — jump straight to the fence's end in one binary search.
            if (TryGetContainingCodeBlock(position, codeBlocks, out var blockEnd))
            {
                position = blockEnd;
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

        // Skip past the Starlight "[Section titled <title>]" anchor stub that
        // appears immediately after the heading text in minified output. For
        // example, the raw bytes around an H2 look like:
        //
        //   "## Connection string[Section titled Connection string] ..."
        //
        // We've already taken "Connection string" as the heading; here we just
        // advance absoluteEnd past the closing ']' so the next heading scan
        // doesn't start inside the anchor stub.
        // See https://github.com/delucis/starlight-llms-txt for the emitter.
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
    /// Finds the next inline heading marker — a space followed by two or more
    /// <c>#</c> characters — used by the minified llms.txt format where headings
    /// share a line with body content.
    /// </summary>
    /// <remarks>
    /// Matches the boundary between body text and an inline heading. Example raw
    /// span (one physical line):
    /// <code>
    /// "Body text for the previous section. ## Next Section [Section titled Next Section]"
    /// </code>
    /// The match position is the space before <c>##</c>, so callers can advance
    /// past it to land on the <c>##</c>. A leading space is required to avoid
    /// matching inside identifiers, URLs, or code-like prose (for example
    /// <c>"file##fragment"</c>).
    /// </remarks>
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
    /// Generates a slug from <paramref name="title"/> and disambiguates it against
    /// <paramref name="slugCounts"/>. If the base slug has already been issued, returns the
    /// next available numeric suffix.
    /// </summary>
    /// <remarks>
    /// The live llms-full.txt corpus has slug collisions caused by titles that differ only in
    /// letter case (for example <c>"Azure Cosmos DB Client integration"</c> versus
    /// <c>"Azure Cosmos DB client integration"</c>). Without disambiguation the second document
    /// is unreachable via <c>aspire docs get &lt;slug&gt;</c>.
    /// </remarks>
    private static string GenerateUniqueSlug(string title, Dictionary<string, int>? slugCounts)
    {
        var baseSlug = GenerateSlug(title);
        if (slugCounts is null)
        {
            return baseSlug;
        }

        if (slugCounts.TryGetValue(baseSlug, out var count))
        {
            var next = count + 1;
            while (slugCounts.ContainsKey($"{baseSlug}-{next}"))
            {
                next++;
            }

            slugCounts[baseSlug] = next;
            var disambiguated = $"{baseSlug}-{next}";
            slugCounts[disambiguated] = 1;
            return disambiguated;
        }

        slugCounts[baseSlug] = 1;
        return baseSlug;
    }

    /// <summary>
    /// Generates a URL-friendly slug from a title.
    /// </summary>
    private static string GenerateSlug(string title)
    {
        // Fast path: if the title is already slug-shaped (lowercase letters,
        // digits, and hyphens — no spaces, no uppercase, no punctuation) just
        // return the original string. This avoids renting/copying into a pooled
        // buffer for titles that already look like slugs (e.g. caller-supplied
        // identifiers in tests).
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
