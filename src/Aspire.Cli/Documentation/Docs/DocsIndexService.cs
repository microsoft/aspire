// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.Docs;

/// <summary>
/// Service for indexing and searching aspire.dev documentation using lexical search.
/// </summary>
internal interface IDocsIndexService
{
    /// <summary>
    /// Gets a value indicating whether the documentation has been indexed.
    /// </summary>
    bool IsIndexed { get; }

    /// <summary>
    /// Ensures documentation is loaded and indexed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all available documents.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of available documents.</returns>
    ValueTask<IReadOnlyList<DocsListItem>> ListDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches documents using weighted lexical matching.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Ranked search results with matched sections.</returns>
    ValueTask<IReadOnlyList<DocsSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a document by slug, optionally returning only a specific section.
    /// </summary>
    /// <param name="slug">The document slug.</param>
    /// <param name="section">Optional section heading to return only that section.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The document content, or null if not found.</returns>
    ValueTask<DocsContent?> GetDocumentAsync(string slug, string? section = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a document in the list.
/// </summary>
internal sealed class DocsListItem
{
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public string? Summary { get; init; }
}

/// <summary>
/// Represents a search result with matched section.
/// </summary>
internal sealed class DocsSearchResult
{
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public string? Summary { get; init; }
    public string? MatchedSection { get; init; }
    public required float Score { get; init; }
}

/// <summary>
/// Represents document content with available sections.
/// </summary>
internal sealed class DocsContent
{
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public string? Summary { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<string> Sections { get; init; }
}

/// <summary>
/// Lexical search implementation using weighted field matching.
/// </summary>
/// <remarks>
/// For technical documentation, lexical search outperforms embeddings because queries are:
/// - Term-driven ("connection string", "workload identity")
/// - Section-oriented ("configuration", "examples")
/// - Name-exact ("Redis resource", "AddServiceDefaults")
/// </remarks>
internal sealed partial class DocsIndexService(IDocsFetcher docsFetcher, IDocsCache docsCache, IConfiguration configuration, ILogger<DocsIndexService> logger) : IDocsIndexService
{
    // Field weights for relevance scoring
    private const float TitleWeight = 10.0f;      // H1 (page title)
    private const float SummaryWeight = 8.0f;     // Blockquote summary
    private const float HeadingWeight = 6.0f;     // H2/H3 headings
    private const float CodeWeight = 5.0f;        // Code identifiers
    private const float BodyWeight = 1.0f;        // Body text

    // Scoring constants
    private const float BaseMatchScore = 1.0f;
    private const float WordBoundaryBonus = 0.5f;
    private const float MultipleOccurrenceBonus = 0.25f;
    private const int MaxOccurrenceBonus = 3;
    private const float CodeIdentifierBonus = 0.5f;
    private const int MinTokenLength = 2;

    // Slug matching bonuses - helps dedicated docs rank higher than incidental mentions
    private const float ExactSlugMatchBonus = 50.0f;        // Query exactly matches slug (e.g., "service-discovery" matches service-discovery)
    private const float FullPhraseInSlugBonus = 30.0f;      // All query words in slug (e.g., "service discovery" -> service-discovery)
    private const float PartialSlugMatchBonus = 10.0f;      // Some query words in slug

    // Changelog/What's New penalty - these pages mention many terms and shouldn't outrank dedicated docs
    private const float WhatsNewPenaltyMultiplier = 0.3f;   // Apply 0.3x to whats-new pages

    // Cache schema version for the LlmsTxtParser output. Bump this constant whenever a
    // change to LlmsTxtParser would produce different indexed documents for the same
    // input (slug shape, content slicing, section structure, etc.). The fingerprint
    // mixes this version into the hash so previously-cached indices are invalidated on
    // first launch with the new CLI; otherwise users keep stale slugs/content until the
    // upstream llms-full.txt changes.
    //   v1 — original LlmsTxtParser output.
    //   v2 — slug disambiguation suffixes + fenced-bash-comment H1 fix.
    private const int IndexSchemaVersion = 2;

    private readonly IDocsFetcher _docsFetcher = docsFetcher;
    private readonly IDocsCache _docsCache = docsCache;
    private readonly string _llmsTxtUrl = DocsSourceConfiguration.GetLlmsTxtUrl(configuration);
    private readonly ILogger<DocsIndexService> _logger = logger;

    // Volatile ensures the double-checked locking pattern works correctly by preventing
    // instruction reordering that could expose a partially-constructed list to other threads.
    private volatile List<IndexedDocument>? _indexedDocuments;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    /// <inheritdoc />
    public bool IsIndexed => _indexedDocuments is not null;

    public async ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default)
    {
        if (_indexedDocuments is not null)
        {
            return;
        }

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_indexedDocuments is not null)
            {
                return;
            }

            var startTimestamp = Stopwatch.GetTimestamp();

            _logger.LogDebug("Loading aspire.dev documentation");

            var cachedDocuments = await _docsCache.GetIndexAsync(cancellationToken).ConfigureAwait(false);
            var cachedFingerprint = await _docsCache.GetIndexSourceFingerprintAsync(cancellationToken).ConfigureAwait(false);

            var content = await _docsFetcher.FetchDocsAsync(cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                if (cachedDocuments is not null)
                {
                    _indexedDocuments = [.. cachedDocuments.Select(static d => new IndexedDocument(d))];

                    _logger.LogWarning(
                        "Failed to refresh Aspire documentation. Using cached index with {Count} documents.",
                        cachedDocuments.Length);

                    return;
                }

                _logger.LogWarning("Failed to fetch documentation");

                return;
            }

            var currentFingerprint = SourceContentFingerprint.Compute(content, IndexSchemaVersion);
            if (cachedDocuments is not null && string.Equals(cachedFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                _indexedDocuments = [.. cachedDocuments.Select(static d => new IndexedDocument(d))];

                var cacheElapsedTime = Stopwatch.GetElapsedTime(startTimestamp);
                _logger.LogInformation("Loaded {Count} documents from cache in {ElapsedTime:ss\\.fff} seconds.", _indexedDocuments.Count, cacheElapsedTime);
                return;
            }

            var documents = await LlmsTxtParser.ParseAsync(content, cancellationToken).ConfigureAwait(false);

            // Pre-compute lowercase versions for faster searching
            _indexedDocuments = [.. documents.Select(static d => new IndexedDocument(d))];

            // Cache the parsed documents for next time
            await _docsCache.SetIndexAsync([.. documents], cancellationToken).ConfigureAwait(false);
            await _docsCache.SetIndexSourceFingerprintAsync(currentFingerprint, cancellationToken).ConfigureAwait(false);

            var elapsedTime = Stopwatch.GetElapsedTime(startTimestamp);

            _logger.LogInformation("Indexed {Count} documents from aspire.dev in {ElapsedTime:ss\\.fff} seconds.", _indexedDocuments.Count, elapsedTime);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<DocsListItem>> ListDocumentsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_indexedDocuments is null or { Count: 0 })
        {
            return [];
        }

        return
        [
            .. _indexedDocuments.Select(static d => new DocsListItem
            {
                Title = d.Source.Title,
                Slug = d.Source.Slug,
                Summary = d.Source.Summary
            })
        ];
    }

    public async ValueTask<IReadOnlyList<DocsSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_indexedDocuments is null or { Count: 0 } || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var queryTokens = Tokenize(query);
        if (queryTokens.Length is 0)
        {
            return [];
        }

        // Pre-compute queryAsSlug once to avoid repeated allocation in hot path
        var queryAsSlug = string.Join("-", queryTokens);

        var results = new List<DocsSearchResult>();

        foreach (var doc in _indexedDocuments)
        {
            // Early reject: if NONE of the query tokens appear anywhere in the doc's
            // concatenated searchable text, ScoreDocument cannot produce a positive
            // score. Every scoring path (slug, title, summary, section heading/body,
            // code spans, identifiers) is a substring of AllSearchableTextLower —
            // see IndexedDocument ctor for the correctness argument. A single
            // IndexOf per token on a ~10KB string is much cheaper than walking every
            // section's content + headings + code spans + identifiers.
            var allText = doc.AllSearchableTextLower.AsSpan();
            var anyTokenMatches = false;
            foreach (var token in queryTokens)
            {
                if (allText.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    anyTokenMatches = true;
                    break;
                }
            }

            if (!anyTokenMatches)
            {
                continue;
            }

            var (score, matchedSection) = ScoreDocument(doc, queryTokens, queryAsSlug);

            if (score > 0)
            {
                results.Add(new DocsSearchResult
                {
                    Title = doc.Source.Title,
                    Slug = doc.Source.Slug,
                    Summary = doc.Source.Summary,
                    MatchedSection = matchedSection,
                    Score = score
                });
            }
        }

        return
        [
            .. results
                .OrderByDescending(static r => r.Score)
                .Take(topK)
        ];
    }

    public async ValueTask<DocsContent?> GetDocumentAsync(string slug, string? section = null, CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_indexedDocuments is null or { Count: 0 })
        {
            return null;
        }

        var doc = _indexedDocuments.FirstOrDefault(d =>
            d.Source.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

        if (doc is null)
        {
            return null;
        }

        var content = doc.Source.Content;

        // If a section is specified, return only that section
        if (!string.IsNullOrEmpty(section))
        {
            var matchedSection = doc.Source.Sections.FirstOrDefault(s =>
                s.Heading.Equals(section, StringComparison.OrdinalIgnoreCase) ||
                s.Heading.Contains(section, StringComparison.OrdinalIgnoreCase));

            if (matchedSection is not null)
            {
                content = matchedSection.Content;
            }
        }

        content = NormalizeContent(content);
        content = DocsSourceConfiguration.RewriteMarkdownLinks(content, _llmsTxtUrl);

        return new DocsContent
        {
            Title = doc.Source.Title,
            Slug = doc.Source.Slug,
            Summary = doc.Source.Summary,
            Content = content,
            Sections = [.. doc.Source.Sections.Select(static s => s.Heading)]
        };
    }

    private static (float Score, string? MatchedSection) ScoreDocument(IndexedDocument doc, string[] queryTokens, string queryAsSlug)
    {
        var score = 0.0f;
        string? matchedSection = null;
        var bestSectionScore = 0.0f;

        // Score slug matching - this is key for finding dedicated docs
        // e.g., query "service discovery" should match slug "service-discovery" with high score
        score += ScoreSlugMatch(doc.SlugLower, doc.SlugSegments, queryTokens, queryAsSlug);

        // Score H1 title
        score += ScoreField(doc.TitleLower, queryTokens) * TitleWeight;

        // Score blockquote summary
        if (doc.SummaryLower is not null)
        {
            score += ScoreField(doc.SummaryLower, queryTokens) * SummaryWeight;
        }

        // Score each section (H2/H3 headings + content)
        for (var i = 0; i < doc.Sections.Count; i++)
        {
            var section = doc.Sections[i];
            var headingScore = ScoreField(section.HeadingLower, queryTokens) * HeadingWeight;
            var codeScore = ScoreCodeIdentifiers(section.CodeSpans, section.Identifiers, queryTokens) * CodeWeight;
            var bodyScore = ScoreField(section.ContentLower, queryTokens) * BodyWeight;

            var sectionScore = headingScore + codeScore + bodyScore;

            if (sectionScore > bestSectionScore)
            {
                bestSectionScore = sectionScore;
                matchedSection = doc.Source.Sections[i].Heading;
            }
        }

        score += bestSectionScore;

        // Apply penalty for "What's New" / changelog pages
        // These pages mention many features and shouldn't outrank dedicated documentation
        // BUT: Skip penalty when user is explicitly searching for changelog content
        // Note: "what's" tokenizes to "what" due to apostrophe splitting, so we check for both "what" and "new" together
        var hasChangelogToken = queryTokens.Any(static t => t is "changelog" or "whats-new");
        var hasWhatsNewTokens = queryTokens.Contains("what") && queryTokens.Contains("new");
        var queryIsAboutChangelog = hasChangelogToken || hasWhatsNewTokens;
        if (!queryIsAboutChangelog && (doc.SlugLower.Contains("whats-new") || doc.SlugLower.Contains("changelog")))
        {
            score *= WhatsNewPenaltyMultiplier;
        }

        return (score, matchedSection);
    }

    /// <summary>
    /// Scores how well the query matches the document slug.
    /// Helps dedicated docs rank higher than docs with incidental mentions.
    /// </summary>
    private static float ScoreSlugMatch(string slugLower, string[] slugSegments, string[] queryTokens, string queryAsSlug)
    {
        if (slugLower.Length is 0 || queryTokens.Length is 0)
        {
            return 0;
        }

        // queryAsSlug is pre-computed before the scoring loop to avoid repeated allocation
        // e.g., ["service", "discovery"] -> "service-discovery"

        // Exact match: query "service-discovery" matches slug "service-discovery"
        if (slugLower == queryAsSlug)
        {
            return ExactSlugMatchBonus;
        }

        // Check if slug contains the full query phrase
        // This handles both multi-word queries and hyphenated single-token queries
        // e.g., slug "azure-service-discovery" contains "service-discovery"
        // e.g., single token "service-bus" matches slug "azure-service-bus"
        var isMultiWordQuery = queryTokens.Length > 1;
        var hasHyphenatedToken = queryTokens.Any(static t => t.Contains('-'));

        if ((isMultiWordQuery || hasHyphenatedToken) && slugLower.Contains(queryAsSlug))
        {
            return FullPhraseInSlugBonus;
        }

        // Count how many query tokens appear as distinct slug segments
        // This prevents "service discovery" from boosting "azure-service-bus"
        // because "discovery" must be a segment, not just "service"
        // Note: slugSegments is pre-computed to avoid allocation in hot path
        var matchingSegments = 0;

        foreach (var token in queryTokens)
        {
            // For hyphenated tokens, check if all parts match consecutive segments in order
            if (token.Contains('-'))
            {
                var tokenParts = token.Split('-');

                // Look for a contiguous sequence of slug segments that matches all token parts
                var foundContiguousMatch = false;
                var maxStartIndex = slugSegments.Length - tokenParts.Length;

                for (var startIndex = 0; startIndex <= maxStartIndex; startIndex++)
                {
                    var allPartsMatch = true;

                    for (var partIndex = 0; partIndex < tokenParts.Length; partIndex++)
                    {
                        if (slugSegments[startIndex + partIndex] != tokenParts[partIndex])
                        {
                            allPartsMatch = false;
                            break;
                        }
                    }

                    if (allPartsMatch)
                    {
                        foundContiguousMatch = true;
                        break;
                    }
                }

                if (foundContiguousMatch)
                {
                    matchingSegments++;
                }
            }
            else
            {
                foreach (var segment in slugSegments)
                {
                    if (segment == token)
                    {
                        matchingSegments++;
                        break;
                    }
                }
            }
        }

        // All tokens match as individual segments (but not necessarily as a contiguous phrase)
        // e.g., query "azure cosmos" matches slug "azure-cosmos-db" segment-by-segment
        // This gets PartialSlugMatchBonus because the full phrase isn't in the slug
        if (matchingSegments == queryTokens.Length)
        {
            return PartialSlugMatchBonus;
        }

        // Some tokens match slug segments - give proportional bonus
        if (matchingSegments > 0)
        {
            // Give proportional bonus based on how many tokens matched
            return PartialSlugMatchBonus * matchingSegments / (float)queryTokens.Length;
        }

        return 0;
    }

    /// <summary>
    /// Tokenizes a query string, preserving symbols like --flag, AddRedis, aspire.json.
    /// </summary>
    private static string[] Tokenize(string text)
        => LexicalScoring.Tokenize(text, TokenSplitRegex(), MinTokenLength);

    /// <summary>
    /// Scores how well a pre-lowercased field matches the query tokens.
    /// </summary>
    private static float ScoreField(string lowerText, string[] queryTokens)
        => LexicalScoring.ScoreField(
            lowerText,
            queryTokens,
            MaxOccurrenceBonus,
            BaseMatchScore,
            WordBoundaryBonus,
            MultipleOccurrenceBonus);

    /// <summary>
    /// Scores pre-extracted code identifiers against query tokens.
    /// </summary>
    private static float ScoreCodeIdentifiers(IReadOnlyList<string> codeSpans, IReadOnlyList<string> identifiers, string[] queryTokens)
    {
        var score = 0.0f;

        // Score backticked code spans
        foreach (var code in codeSpans)
        {
            foreach (var token in queryTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    score += BaseMatchScore;
                }
            }
        }

        // Score PascalCase identifiers
        foreach (var identifier in identifiers)
        {
            foreach (var token in queryTokens)
            {
                if (identifier.Contains(token, StringComparison.Ordinal))
                {
                    score += CodeIdentifierBonus;
                }
            }
        }

        return score;
    }

    /// <summary>
    /// Normalizes markdown content from llms.txt sources.
    /// The llms.txt format strips most whitespace from markdown, collapsing headings,
    /// lists, tables, and code blocks onto fewer lines. This method re-introduces
    /// the blank lines and line breaks needed so the text parses as valid markdown.
    /// </summary>
    internal static string NormalizeContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        // Replace allocates only when a match is found. The CRLF/CR replacements are
        // already cheap when absent (llms.txt is LF-only on the wire), so don't bother
        // gating them.
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        var builder = new StringBuilder(content.Length + 64);
        var position = 0;

        foreach (Match match in MarkdownFenceBlockRegex().Matches(content))
        {
            builder.Append(NormalizeMarkdownSegment(content[position..match.Index]));
            builder.Append(NormalizeCodeBlock(match.Value));
            position = match.Index + match.Length;
        }

        builder.Append(NormalizeMarkdownSegment(content[position..]));

        // Each Regex.Replace in this chain allocates a fresh string the size of the
        // input when ANY match is found, even if only one. For long documents this is
        // the dominant allocation source in GetDocumentAsync (≈1.4 MB per call before
        // these IsMatch gates). For docs that don't contain tables/lists/etc. the
        // corresponding pass becomes a single forward scan with no allocation.
        var normalized = ReplaceIfMatches(builder.ToString(), TrailingWhitespaceBeforeNewlineRegex(), "\n");
        normalized = ReplaceIfMatches(normalized, BlankLineAfterHeadingRegex(), "$1\n");
        normalized = ReplaceIfMatches(normalized, BlankLineAfterTableRegex(), "$1\n");
        normalized = ReplaceIfMatches(normalized, BlankLineAfterListRegex(), "$1\n");
        normalized = ReplaceIfMatches(normalized, ExcessBlankLinesRegex(), "\n\n");

        return normalized.Trim();
    }

    private static string NormalizeMarkdownSegment(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        // See NormalizeContent for the rationale: IsMatch is a no-allocation scan
        // that short-circuits at the first match, so when a pattern doesn't apply
        // we avoid both the rebuild and the allocation.
        content = ReplaceIfMatches(content, InlineHeadingRegex(), "\n\n$1");
        content = ReplaceIfMatches(content, SectionTitledBookmarkRegex(), "\n\n");
        content = ReplaceIfMatches(content, InlineOrderedListRegex(), "\n$1");
        content = ReplaceIfMatches(content, InlineUnorderedListRegex(), "\n* ");
        content = ReplaceIfMatches(content, InlineTableStartRegex(), "$1\n$2");
        content = ReplaceIfMatches(content, InlineTableRowBoundaryRegex(), "\n");
        content = ReplaceIfMatches(content, InlineTableEndRegex(), "$1\n$2");
        content = ReplaceIfMatches(content, LeadingWhitespaceRegex(), "");

        return content;
    }

    private static string ReplaceIfMatches(string input, Regex regex, string replacement)
        => regex.IsMatch(input) ? regex.Replace(input, replacement) : input;

    private static string NormalizeCodeBlock(string codeBlock)
    {
        var trimmed = codeBlock.Trim();
        if (trimmed.Length is 0)
        {
            return codeBlock;
        }

        var content = trimmed[3..^3].Trim();
        if (!content.Contains('\n'))
        {
            var firstWhitespace = content.IndexOfAny([' ', '\t']);
            if (firstWhitespace > 0)
            {
                var language = content[..firstWhitespace];
                var code = content[(firstWhitespace + 1)..].Trim();
                if (code.Length > 0 && IsLikelyCodeFenceLanguage(language))
                {
                    return $"\n```{language}\n{code}\n```\n";
                }
            }

            return $"\n```\n{content}\n```\n";
        }

        return $"\n{trimmed}\n";
    }

    private static bool IsLikelyCodeFenceLanguage(string language)
        => language.ToLowerInvariant() is
            "bash" or "sh" or "shell" or "cmd" or "powershell" or "ps1" or
            "javascript" or "js" or "typescript" or "ts" or "jsx" or "tsx" or
            "python" or "py" or "csharp" or "cs" or "go" or "rust" or "java" or
            "json" or "yaml" or "yml" or
            "xml" or "html" or "css" or "sql" or "typescript/nodejs";

    // Split on whitespace and punctuation, keeping dotted/hyphenated tokens together
    [GeneratedRegex(@"[\s,;:!?\(\)\[\]{}""']+")]
    private static partial Regex TokenSplitRegex();

    // Match backticked code spans
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeBlockRegex();

    // Match PascalCase/camelCase identifiers
    [GeneratedRegex(@"\b[A-Z][a-zA-Z0-9]+\b")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex MarkdownFenceBlockRegex();

    [GeneratedRegex(@"(?<=\S)\s+(#{2,6}\s)")]
    private static partial Regex InlineHeadingRegex();

    [GeneratedRegex(@"\s*\[Section titled[^\]]*\]\(#(?:[^)]+)\)\s*")]
    private static partial Regex SectionTitledBookmarkRegex();

    [GeneratedRegex(@"(?<=\S)\s+(\d+\.\s+)")]
    private static partial Regex InlineOrderedListRegex();

    [GeneratedRegex(@"(?<=\S)\s+\*\s+")]
    private static partial Regex InlineUnorderedListRegex();

    [GeneratedRegex(@"(\S)\s+(\|(?:[^|\n]*\|){2,})")]
    private static partial Regex InlineTableStartRegex();

    [GeneratedRegex(@"(?<=\|)\s+(?=\|)")]
    private static partial Regex InlineTableRowBoundaryRegex();

    [GeneratedRegex(@"(\|(?:[^|\n]*\|){2,})\s+([^\s|][^|\n]*)$", RegexOptions.Multiline)]
    private static partial Regex InlineTableEndRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingWhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();

    [GeneratedRegex(@"(?m)^[ \t]+")]
    private static partial Regex LeadingWhitespaceRegex();

    [GeneratedRegex(@"(?m)(^#{1,6}\s+[^\n]+\n)(?!\n|#|```)")]
    private static partial Regex BlankLineAfterHeadingRegex();

    [GeneratedRegex(@"(?m)(^\|[^\n]*\|[^\n]*\n)(?!\n|\||```)")]
    private static partial Regex BlankLineAfterTableRegex();

    [GeneratedRegex(@"(?m)(^(?:\*\s|\d+\.\s)[^\n]*\n)(?!\n|\*\s|\d+\.\s|```)")]
    private static partial Regex BlankLineAfterListRegex();

    /// <summary>
    /// Pre-indexed document with lowercase text for faster searching.
    /// </summary>
    private sealed class IndexedDocument
    {
        private readonly string _slugLower;

        public IndexedDocument(LlmsDocument source)
        {
            Source = source;
            TitleLower = source.Title.ToLowerInvariant();
            _slugLower = source.Slug.ToLowerInvariant();
            SlugSegments = _slugLower.Split('-');
            SummaryLower = source.Summary?.ToLowerInvariant();
            Sections = [.. source.Sections.Select(static s => new IndexedSection(s))];

            // Build a single concatenated lowercase haystack used ONLY as an early-reject
            // pre-filter in SearchAsync. Probing each query token against every section's
            // ContentLower scales with every section in every document; one per-doc haystack
            // check lets us skip the full per-section scoring for docs that can't possibly
            // match.
            //
            // CORRECTNESS: We include every substring that ScoreDocument could ever match:
            //   - SlugLower (ScoreSlugMatch)
            //   - TitleLower (ScoreField on title)
            //   - SummaryLower (ScoreField on summary)
            //   - Each section's HeadingLower + ContentLower (ScoreField)
            //   - CodeSpans + Identifiers are already substrings of section.ContentLower
            //     (the extraction regexes pull text directly out of source.Content), so
            //     they don't need to be appended separately.
            // A space separator between fields prevents tokens from spanning two unrelated
            // fields (e.g., end of title + start of summary).
            var capacity = _slugLower.Length + TitleLower.Length + (SummaryLower?.Length ?? 0);
            foreach (var section in Sections)
            {
                capacity += section.HeadingLower.Length + section.ContentLower.Length + 2;
            }

            var builder = new StringBuilder(capacity + 4);
            builder.Append(_slugLower);
            builder.Append(' ');
            builder.Append(TitleLower);
            if (SummaryLower is not null)
            {
                builder.Append(' ');
                builder.Append(SummaryLower);
            }

            foreach (var section in Sections)
            {
                builder.Append(' ');
                builder.Append(section.HeadingLower);
                builder.Append(' ');
                builder.Append(section.ContentLower);
            }

            AllSearchableTextLower = builder.ToString();
        }

        public LlmsDocument Source { get; }

        public string TitleLower { get; }

        public string SlugLower => _slugLower;

        /// <summary>
        /// Pre-computed slug segments to avoid allocation in hot path during scoring.
        /// </summary>
        public string[] SlugSegments { get; }

        public string? SummaryLower { get; }

        public IReadOnlyList<IndexedSection> Sections { get; }

        /// <summary>
        /// Concatenated lowercase text of every searchable field (slug, title, summary,
        /// each section heading + content), separated by single spaces. Used by
        /// <c>SearchAsync</c> as a fast reject filter: if none of the query tokens appear
        /// anywhere in this haystack, the document cannot score &gt; 0 and we skip the
        /// per-section scoring loop. Does NOT participate in scoring itself.
        /// </summary>
        public string AllSearchableTextLower { get; }
    }

    /// <summary>
    /// Pre-indexed section with extracted code identifiers.
    /// </summary>
    private sealed class IndexedSection(LlmsSection source)
    {
        public string HeadingLower { get; } = source.Heading.ToLowerInvariant();

        public string ContentLower { get; } = source.Content.ToLowerInvariant();

        public IReadOnlyList<string> CodeSpans { get; } =
        [
            .. CodeBlockRegex()
                .Matches(source.Content)
                .Select(static m => m.Groups[1].Value.ToLowerInvariant())
        ];

        public IReadOnlyList<string> Identifiers { get; } =
        [
            .. IdentifierRegex()
                .Matches(source.Content)
                .Select(static m => m.Value.ToLowerInvariant())
        ];
    }
}
