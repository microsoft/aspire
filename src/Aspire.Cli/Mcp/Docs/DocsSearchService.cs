// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Mcp.Docs;

/// <summary>
/// Service for searching Aspire documentation.
/// </summary>
internal interface IDocsSearchService
{
    /// <summary>
    /// Searches the Aspire documentation for content relevant to the given query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The search results, or null if documentation is unavailable.</returns>
    Task<DocsSearchResponse?> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a response from the documentation search service.
/// </summary>
internal sealed class DocsSearchResponse
{
    /// <summary>
    /// Gets or sets the original query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Gets or sets the search results.
    /// </summary>
    public required IReadOnlyList<SearchResult> Results { get; init; }

    /// <summary>
    /// Formats the results as markdown for display.
    /// </summary>
    /// <param name="title">Optional title for the results section.</param>
    /// <param name="showScores">Whether to show similarity scores.</param>
    /// <returns>Formatted markdown string.</returns>
    public string FormatAsMarkdown(string? title = null, bool showScores = false)
    {
        if (Results.Count is 0)
        {
            return $"No results found for query: '{Query}'. Try rephrasing your question.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {title ?? $"Documentation for: \"{Query}\""}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {Results.Count} relevant documentation snippets:");
        sb.AppendLine();

        for (var i = 0; i < Results.Count; i++)
        {
            var result = Results[i];

            if (showScores)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"## {result.Title} (Score: {result.Score:F3})");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"## {result.Title}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"**Slug:** `{result.Slug}`");

            if (!string.IsNullOrEmpty(result.Section))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Section:** {result.Section}");
            }

            sb.AppendLine();
            sb.AppendLine(result.Content);
            sb.AppendLine();
            sb.AppendLine("---");
        }

        sb.AppendLine("Use `get_doc` with the slug to retrieve the full content of this page.");
        sb.AppendLine();
        return sb.ToString();
    }
}

/// <summary>
/// Represents a search result from the documentation.
/// </summary>
internal sealed class SearchResult
{
    /// <summary>
    /// Gets the title of the matched document.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the slug of the matched document, used with get_doc to retrieve full content.
    /// </summary>
    public required string Slug { get; init; }

    /// <summary>
    /// Gets the matched content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets the section where the match was found.
    /// </summary>
    public string? Section { get; init; }

    /// <summary>
    /// Gets the relevance score.
    /// </summary>
    public float Score { get; init; }
}

/// <summary>
/// Implementation of <see cref="IDocsSearchService"/> using lexical search.
/// </summary>
internal sealed class DocsSearchService(
    IDocsIndexService docsIndexService,
    ILogger<DocsSearchService> logger) : IDocsSearchService
{
    private readonly IDocsIndexService _docsIndexService = docsIndexService;
    private readonly ILogger<DocsSearchService> _logger = logger;

    public async Task<DocsSearchResponse?> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        // Use lexical search from the index service
        var searchResults = await _docsIndexService.SearchAsync(query, topK, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Search for '{Query}' returned {Count} results", query, searchResults.Count);

        // Convert DocsSearchResult to SearchResult
        var results = searchResults.Select(r => new SearchResult
        {
            Title = r.Title,
            Slug = r.Slug,
            Content = r.Summary ?? r.Title,
            Section = r.MatchedSection,
            Score = r.Score
        }).ToList();

        return new DocsSearchResponse
        {
            Query = query,
            Results = results
        };
    }
}
