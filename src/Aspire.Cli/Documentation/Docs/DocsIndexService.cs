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
// `aspire docs list --format json` uses this shape; keep docs/specs/cli-output-formats.md in sync when changing it.
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
// `aspire docs get --format json` uses this shape; keep docs/specs/cli-output-formats.md in sync when changing it.
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
    private const float TitlePhraseBonus = 40.0f;   // Exact query phrase in H1 title
    private const float SummaryPhraseBonus = 20.0f; // Exact query phrase in blockquote summary
    private const float HeadingPhraseBonus = 15.0f; // Exact query phrase in H2/H3 heading

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

    // Volatile ensures the double-checked locking pattern publishes the fully initialized
    // index data before any thread observes _indexedDocuments as non-null.
    private volatile List<IndexedDocument>? _indexedDocuments;
    private volatile Dictionary<string, float>? _tokenIdf;
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
                    SetIndexedDocuments(cachedDocuments);

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
                var indexedDocuments = SetIndexedDocuments(cachedDocuments);

                var cacheElapsedTime = Stopwatch.GetElapsedTime(startTimestamp);
                _logger.LogInformation("Loaded {Count} documents from cache in {ElapsedTime:ss\\.fff} seconds.", indexedDocuments.Count, cacheElapsedTime);
                return;
            }

            var documents = await LlmsTxtParser.ParseAsync(content, cancellationToken).ConfigureAwait(false);

            // Pre-compute normalized versions for faster searching
            var indexedDocumentsFromSource = SetIndexedDocuments(documents);

            // Cache the parsed documents for next time
            await _docsCache.SetIndexAsync([.. documents], cancellationToken).ConfigureAwait(false);
            await _docsCache.SetIndexSourceFingerprintAsync(currentFingerprint, cancellationToken).ConfigureAwait(false);

            var elapsedTime = Stopwatch.GetElapsedTime(startTimestamp);

            _logger.LogInformation("Indexed {Count} documents from aspire.dev in {ElapsedTime:ss\\.fff} seconds.", indexedDocumentsFromSource.Count, elapsedTime);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private List<IndexedDocument> SetIndexedDocuments(IEnumerable<LlmsDocument> documents)
    {
        var indexedDocuments = documents.Select(static d => new IndexedDocument(d)).ToList();
        _tokenIdf = BuildTokenIdf(indexedDocuments);
        _indexedDocuments = indexedDocuments;
        return indexedDocuments;
    }

    private static Dictionary<string, float> BuildTokenIdf(IReadOnlyList<IndexedDocument> documents)
    {
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var documentTokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            documentTokens.Clear();

            // IDF is based on whether a token appears in a document, not how many times
            // it appears inside that document. That keeps noisy pages from dominating by
            // repetition: "azure" or "new" appears almost everywhere and should carry a
            // small multiplier, while rarer terms like "addredis" or "134" are stronger
            // signals for queries such as "AddRedis" or "whats new 13.4".
            foreach (var token in Tokenize(document.AllSearchableTextLower))
            {
                documentTokens.Add(token);
            }

            // Tokenize keeps hyphenated slugs as one token, so add slug segments too.
            // For example, "service-discovery" should contribute weights for both
            // "service" and "discovery" when scoring the query "service discovery".
            foreach (var segment in document.SlugSegments)
            {
                if (segment.Length >= MinTokenLength)
                {
                    documentTokens.Add(segment);
                }
            }

            foreach (var token in documentTokens)
            {
                documentFrequency[token] = documentFrequency.TryGetValue(token, out var count) ? count + 1 : 1;
            }
        }

        var idf = new Dictionary<string, float>(documentFrequency.Count, StringComparer.Ordinal);
        var documentCount = documents.Count;
        foreach (var (token, count) in documentFrequency)
        {
            // In plain terms, IDF is a rarity multiplier. A term that appears in
            // nearly every doc, like "new" or "azure", should not move the ranking
            // much by itself. A rarer term, like "addredis" or "134", is stronger
            // evidence that a document is the one the user meant.
            //
            // The formula below is the smoothed BM25-style way to turn that rarity
            // into a small positive weight:
            //   log(1 + (N - df + 0.5) / (df + 0.5))
            // where N is the number of indexed docs and df is the number of docs that contain
            // the token. The 0.5 terms keep very small corpora from producing extreme weights,
            // while the +1 inside the log keeps terms that appear in every doc positive but tiny.
            // We only use the result as a rarity multiplier in the existing field scorer; this is
            // not a full BM25 implementation.
            idf[token] = MathF.Log(1.0f + (documentCount - count + 0.5f) / (count + 0.5f));
        }

        return idf;
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

        if (topK <= 0)
        {
            return [];
        }

        // Pre-compute queryAsSlug once to avoid repeated allocation in hot path
        var queryAsSlug = string.Join("-", queryTokens);
        var queryAsPhrase = queryTokens.Length > 1 ? string.Join(' ', queryTokens) : string.Empty;
        var queryTokenWeights = GetQueryTokenWeights(queryTokens);

        var results = new List<SearchCandidate>(Math.Min(topK, _indexedDocuments.Count));
        var documentIndex = 0;

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
                documentIndex++;
                continue;
            }

            var (score, matchedSection) = ScoreDocument(doc, queryTokens, queryTokenWeights, queryAsSlug, queryAsPhrase);

            if (score > 0)
            {
                AddTopResult(results, new SearchCandidate(doc, matchedSection, score, documentIndex), topK);
            }

            documentIndex++;
        }

        return
        [
            .. results
                .Select(static r => new DocsSearchResult
                {
                    Title = r.Document.Source.Title,
                    Slug = r.Document.Source.Slug,
                    Summary = r.Document.Source.Summary,
                    MatchedSection = r.MatchedSection,
                    Score = r.Score
                })
        ];
    }

    // SearchAsync calls this for every positive-scoring document. Keep `results` sorted and
    // capped to topK as we scan so we never allocate/sort every matching document. A small
    // sorted list is intentional here: the CLI default is topK = 10, so linear insertion is
    // simple, preserves final score order and stable tie-breaking, and avoids the extra final
    // sort a heap/priority queue would need. If topK ever becomes large, revisit this.
    private static void AddTopResult(List<SearchCandidate> results, SearchCandidate candidate, int topK)
    {
        var insertIndex = 0;
        while (insertIndex < results.Count && !IsBetterCandidate(candidate, results[insertIndex]))
        {
            insertIndex++;
        }

        if (insertIndex >= topK)
        {
            return;
        }

        results.Insert(insertIndex, candidate);
        if (results.Count > topK)
        {
            results.RemoveAt(results.Count - 1);
        }
    }

    private static bool IsBetterCandidate(SearchCandidate candidate, SearchCandidate current)
        => candidate.Score > current.Score ||
           (candidate.Score == current.Score && candidate.DocumentIndex < current.DocumentIndex);

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

    private float[] GetQueryTokenWeights(string[] queryTokens)
    {
        var tokenIdf = _tokenIdf;
        var weights = new float[queryTokens.Length];

        for (var i = 0; i < queryTokens.Length; i++)
        {
            var token = queryTokens[i];
            weights[i] = tokenIdf is not null && tokenIdf.TryGetValue(token, out var idf)
                ? idf
                : 1.0f;
        }

        return weights;
    }

    // ScoreDocument combines two kinds of evidence:
    //   1. Identity signals (slug + H1 title): strong proof that this is the page the user meant.
    //   2. Context signals (summary + best matching section): useful supporting evidence, but
    //      noisier because broad docs and release notes mention many unrelated terms.
    // Field weights make matches in titles/headings/summaries count more than body text, IDF
    // makes rarer query terms count more than common terms, and exact phrase bonuses help pages
    // with the whole query in a title/heading beat pages with scattered incidental matches.
    private static (float Score, string? MatchedSection) ScoreDocument(
        IndexedDocument doc,
        string[] queryTokens,
        float[] queryTokenWeights,
        string queryAsSlug,
        string queryAsPhrase)
    {
        string? matchedSection = null;
        var bestSectionScore = 0.0f;

        // Score slug matching - this is key for finding dedicated docs
        // e.g., query "service discovery" should match slug "service-discovery" with high score
        var slugScore = ScoreSlugMatch(doc.SlugLower, doc.SlugSegments, queryTokens, queryAsSlug);

        // Score H1 title
        var titleScore = (ScoreField(doc.TitleLower, queryTokens, queryTokenWeights) * TitleWeight) +
            ScorePhraseMatch(doc.TitleLower, queryAsPhrase, TitlePhraseBonus);

        // Score blockquote summary
        var summaryScore = 0.0f;
        if (doc.SummaryLower is not null)
        {
            summaryScore = (ScoreField(doc.SummaryLower, queryTokens, queryTokenWeights) * SummaryWeight) +
                ScorePhraseMatch(doc.SummaryLower, queryAsPhrase, SummaryPhraseBonus);
        }

        // Score every section, but only add the best one to the document score. This gives
        // the result a useful MatchedSection without letting a long page win just because it
        // has many sections that each contain a small incidental match.
        for (var i = 0; i < doc.Sections.Count; i++)
        {
            var section = doc.Sections[i];
            var headingScore = (ScoreField(section.HeadingLower, queryTokens, queryTokenWeights) * HeadingWeight) +
                ScorePhraseMatch(section.HeadingLower, queryAsPhrase, HeadingPhraseBonus);
            var codeScore = ScoreCodeIdentifiers(section.CodeSpans, section.Identifiers, queryTokens, queryTokenWeights) * CodeWeight;
            var bodyScore = ScoreField(section.ContentLower, queryTokens, queryTokenWeights) * BodyWeight;

            var sectionScore = headingScore + codeScore + bodyScore;

            if (sectionScore > bestSectionScore)
            {
                bestSectionScore = sectionScore;
                matchedSection = doc.Source.Sections[i].Heading;
            }
        }

        var identityScore = slugScore + titleScore;
        var contextScore = summaryScore + bestSectionScore;

        // What's New and changelog pages contain broad feature lists, so a query like
        // "javascript" or "go" should prefer dedicated docs over release-note context
        // mentions. Keep title/slug identity unpenalized so explicit queries like
        // "whats new 13.4" and "changelog" still find the release-note pages.
        if (IsReleaseNotesDocument(doc) && !HasReleaseNotesIdentityMatch(doc, queryTokens, queryAsSlug, slugScore))
        {
            contextScore *= WhatsNewPenaltyMultiplier;
        }

        return (identityScore + contextScore, matchedSection);
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
        => LexicalScoring.Tokenize(NormalizeSearchText(text), TokenSplitRegex(), MinTokenLength);

    private static bool IsReleaseNotesDocument(IndexedDocument doc)
        => doc.SlugLower.Contains("whats-new", StringComparison.Ordinal) ||
           doc.SlugLower.Contains("changelog", StringComparison.Ordinal);

    private static bool HasReleaseNotesIdentityMatch(IndexedDocument doc, string[] queryTokens, string queryAsSlug, float slugScore)
    {
        if (queryTokens.Length is 0)
        {
            return false;
        }

        // Single-token queries like "new", "whats", or "what" are not specific enough
        // to prove the user wants a release-note page. They only become release-note
        // identity signals when paired with more context, such as "whats new 13.4".
        // Otherwise a broad release-note page could avoid the context penalty for a
        // generic query and outrank a dedicated doc that happens to contain the term.
        if (queryTokens.Length is 1 && IsReleaseNotesGenericToken(queryTokens[0]))
        {
            return false;
        }

        if (doc.SlugLower == queryAsSlug || slugScore >= FullPhraseInSlugBonus)
        {
            return true;
        }

        var matchedTokens = CountDistinctIdentityTokenMatches(doc.IdentitySearchableTextLower, queryTokens);
        // Versioned release-note queries need all tokens so "whats new 13.4" does not
        // rank "What's new in Aspire 13.2" just because "whats" and "new" match.
        // Non-versioned queries allow a partial identity match for longer phrases, but
        // still require token-boundary matches so "go" does not match inside "changelog".
        var requiredMatches = queryTokens.Any(static token => ContainsDigit(token))
            ? queryTokens.Length
            : queryTokens.Length switch
            {
                1 => 1,
                2 => 2,
                _ => Math.Max(2, (queryTokens.Length * 2 + 2) / 3)
            };

        return matchedTokens >= requiredMatches;
    }

    private static bool IsReleaseNotesGenericToken(string token)
        => token is "new" or "whats" or "what";

    private static bool ContainsDigit(string token)
    {
        foreach (var c in token.AsSpan())
        {
            if (char.IsDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountDistinctIdentityTokenMatches(string lowerText, string[] queryTokens)
    {
        var count = 0;
        var textSpan = lowerText.AsSpan();

        foreach (var token in queryTokens)
        {
            if (ContainsIdentityToken(textSpan, token))
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsIdentityToken(ReadOnlySpan<char> textSpan, string token)
    {
        if (ContainsWordBoundaryMatch(textSpan, token))
        {
            return true;
        }

        // The "What's new" title normalizes to "whats new", but users commonly type
        // "what new" without the trailing s. Keep that release-note query working
        // without allowing arbitrary substrings like "go" inside "changelog".
        return token is "what" && ContainsWordBoundaryMatch(textSpan, "whats");
    }

    // ContainsIdentityToken uses this for release-note identity checks. Search every
    // occurrence of the token and accept only lexical word-boundary matches, so "go"
    // matches "Go updates" but not the substring inside "changelog". That keeps short
    // feature names from accidentally making a changelog page look like an explicit
    // release-note query.
    private static bool ContainsWordBoundaryMatch(ReadOnlySpan<char> textSpan, string token)
    {
        var startIndex = 0;

        while (startIndex < textSpan.Length)
        {
            var nextIndex = textSpan[startIndex..].IndexOf(token, StringComparison.Ordinal);
            if (nextIndex < 0)
            {
                return false;
            }

            var absoluteIndex = startIndex + nextIndex;
            if (LexicalScoring.IsWordBoundaryMatch(textSpan, token, absoluteIndex))
            {
                return true;
            }

            startIndex = absoluteIndex + token.Length;
        }

        return false;
    }

    // Apply the same lightweight normalization to indexed document fields and incoming
    // queries before tokenization/scoring. This deliberately handles the cases where the
    // live docs and user input use different spellings for the same intent:
    //   "What's new" -> "whats new"
    //   "13.4" / "13-4" -> "134"
    // That lets a query like "whats new 13.4" line up with both the page title and the
    // aspire.dev slug "whats-new-in-aspire-134" without changing unrelated punctuation.
    private static string NormalizeSearchText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsIgnoredPunctuation(c))
            {
                continue;
            }

            // aspire.dev slugs strip numeric separators from versions, so normalize
            // "13.4" and "13-4" to the same token shape as "134".
            if (IsNumericVersionSeparator(text, i))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static bool IsIgnoredPunctuation(char c)
        => c is '\'' or '\u2019' or '\u2018' or '\u02BC' or '`';

    private static bool IsNumericVersionSeparator(string text, int index)
        => (text[index] is '.' or '-') &&
           index > 0 &&
           index + 1 < text.Length &&
           char.IsDigit(text[index - 1]) &&
           char.IsDigit(text[index + 1]);

    /// <summary>
    /// Scores one normalized field by finding each query token as a substring, rewarding the
    /// first word-boundary match, adding a capped repeated-occurrence bonus, and multiplying
    /// the token's contribution by its IDF weight so rarer query terms count more.
    /// </summary>
    // This is docs-search-specific instead of using LexicalScoring.ScoreField because docs
    // search needs per-token weights. For example, in "whats new 13.4", "new" appears in
    // many docs and should barely move ranking, while "134" is rare and should count more.
    // In "service discovery", a word-boundary match for "discovery" should count more than
    // a substring match inside another word; exact whole-phrase bonuses are added by the
    // ScoreDocument caller for titles, summaries, and headings.
    private static float ScoreField(string lowerText, string[] queryTokens, float[] queryTokenWeights)
    {
        if (string.IsNullOrEmpty(lowerText))
        {
            return 0;
        }

        var score = 0.0f;
        var textSpan = lowerText.AsSpan();
        var occurrenceLimit = MaxOccurrenceBonus + 1;

        for (var i = 0; i < queryTokens.Length; i++)
        {
            var token = queryTokens[i];
            var startIndex = 0;
            var firstMatchIndex = -1;
            var count = 0;

            while (startIndex < textSpan.Length && count < occurrenceLimit)
            {
                var nextIndex = textSpan[startIndex..].IndexOf(token, StringComparison.Ordinal);
                if (nextIndex < 0)
                {
                    break;
                }

                var absoluteIndex = startIndex + nextIndex;
                if (firstMatchIndex < 0)
                {
                    firstMatchIndex = absoluteIndex;
                }

                count++;
                startIndex = absoluteIndex + token.Length;
            }

            if (count is 0)
            {
                continue;
            }

            var tokenScore = BaseMatchScore;
            if (LexicalScoring.IsWordBoundaryMatch(textSpan, token, firstMatchIndex))
            {
                tokenScore += WordBoundaryBonus;
            }

            if (count > 1)
            {
                tokenScore += Math.Min(count - 1, MaxOccurrenceBonus) * MultipleOccurrenceBonus;
            }

            score += tokenScore * queryTokenWeights[i];
        }

        return score;
    }

    private static float ScorePhraseMatch(string lowerText, string queryAsPhrase, float bonus)
        => queryAsPhrase.Length > 0 && lowerText.Contains(queryAsPhrase, StringComparison.Ordinal)
            ? bonus
            : 0;

    /// <summary>
    /// Scores pre-extracted code identifiers against query tokens.
    /// </summary>
    private static float ScoreCodeIdentifiers(
        IReadOnlyList<string> codeSpans,
        IReadOnlyList<string> identifiers,
        string[] queryTokens,
        float[] queryTokenWeights)
    {
        var score = 0.0f;

        for (var i = 0; i < queryTokens.Length; i++)
        {
            var token = queryTokens[i];
            var codeSpanMatches = CountContainingTokens(codeSpans, token);
            if (codeSpanMatches > 0)
            {
                score += ScoreRepeatedMatches(BaseMatchScore, codeSpanMatches) * queryTokenWeights[i];
            }

            var identifierMatches = CountContainingTokens(identifiers, token);
            if (identifierMatches > 0)
            {
                score += ScoreRepeatedMatches(CodeIdentifierBonus, identifierMatches) * queryTokenWeights[i];
            }
        }

        return score;
    }

    private static int CountContainingTokens(IReadOnlyList<string> values, string token)
    {
        var count = 0;
        var occurrenceLimit = MaxOccurrenceBonus + 1;
        foreach (var value in values)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                count++;
                if (count >= occurrenceLimit)
                {
                    break;
                }
            }
        }

        return count;
    }

    private static float ScoreRepeatedMatches(float baseScore, int count)
        => baseScore + Math.Min(count - 1, MaxOccurrenceBonus) * MultipleOccurrenceBonus;

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

    private readonly record struct SearchCandidate(IndexedDocument Document, string? MatchedSection, float Score, int DocumentIndex);

    /// <summary>
    /// Pre-indexed document with normalized search text for faster searching.
    /// </summary>
    private sealed class IndexedDocument
    {
        private readonly string _slugLower;

        public IndexedDocument(LlmsDocument source)
        {
            Source = source;
            TitleLower = NormalizeSearchText(source.Title);
            _slugLower = NormalizeSearchText(source.Slug);
            SlugSegments = _slugLower.Split('-');
            SummaryLower = source.Summary is not null ? NormalizeSearchText(source.Summary) : null;
            Sections = [.. source.Sections.Select(static s => new IndexedSection(s))];
            IdentitySearchableTextLower = $"{_slugLower} {TitleLower}";

            // Build a single concatenated normalized haystack used ONLY as an early-reject
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

        public string IdentitySearchableTextLower { get; }

        /// <summary>
        /// Concatenated normalized text of every searchable field (slug, title, summary,
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
        public string HeadingLower { get; } = NormalizeSearchText(source.Heading);

        public string ContentLower { get; } = NormalizeSearchText(source.Content);

        public IReadOnlyList<string> CodeSpans { get; } =
        [
            .. CodeBlockRegex()
                .Matches(source.Content)
                .Select(static m => NormalizeSearchText(m.Groups[1].Value))
        ];

        public IReadOnlyList<string> Identifiers { get; } =
        [
            .. IdentifierRegex()
                .Matches(source.Content)
                .Select(static m => NormalizeSearchText(m.Value))
        ];
    }
}
