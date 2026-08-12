// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Service for fetching Aspire API reference content.
/// </summary>
internal interface IApiDocsFetcher
{
    /// <summary>
    /// Fetches the sitemap used to build the API catalog.
    /// </summary>
    Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches markdown content for the given API page URL.
    /// </summary>
    Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IApiDocsFetcher"/> with cache-backed ETag support.
/// </summary>
internal sealed class ApiDocsFetcher(HttpClient httpClient, IApiDocsCache cache, IConfiguration configuration, ILogger<ApiDocsFetcher> logger) : IApiDocsFetcher
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IApiDocsCache _cache = cache;
    private readonly string _sitemapUrl = ApiDocsSourceConfiguration.GetSitemapUrl(configuration);
    private readonly string _sitemapCacheKey = ApiDocsSourceConfiguration.GetSitemapContentCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration));
    private readonly ILogger<ApiDocsFetcher> _logger = logger;

    /// <summary>
    /// Fetches the sitemap used to build the API catalog.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sitemap content, or <c>null</c> when it cannot be retrieved.</returns>
    public Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default)
        => CachedHttpDocumentFetcher.FetchAsync(_httpClient, _cache, _sitemapUrl, _sitemapCacheKey, _logger, cancellationToken);

    /// <summary>
    /// Fetches markdown content for the specified API page.
    /// </summary>
    /// <param name="pageUrl">The canonical API page URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The markdown content, or <c>null</c> when it cannot be retrieved.</returns>
    public Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        var markdownUrl = ApiDocsSourceConfiguration.BuildMarkdownUrl(pageUrl, _sitemapUrl);
        var cacheKey = ApiDocsSourceConfiguration.GetPageContentCacheKey(pageUrl, _sitemapUrl);
        return CachedHttpDocumentFetcher.FetchAsync(_httpClient, _cache, markdownUrl, cacheKey, _logger, cancellationToken);
    }
}
