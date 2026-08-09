// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.Docs;

/// <summary>
/// Service for fetching aspire.dev documentation content.
/// </summary>
internal interface IDocsFetcher
{
    /// <summary>
    /// Fetches the small (abridged) documentation content.
    /// Uses ETag-based caching to avoid re-downloading unchanged content.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The documentation content, or null if fetch failed.</returns>
    Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IDocsFetcher"/> that fetches from aspire.dev with ETag caching.
/// </summary>
internal sealed class DocsFetcher(HttpClient httpClient, IDocsCache cache, IConfiguration configuration, ILogger<DocsFetcher> logger) : IDocsFetcher
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IDocsCache _cache = cache;
    private readonly string _llmsTxtUrl = DocsSourceConfiguration.GetLlmsTxtUrl(configuration);
    private readonly string _cacheKey = DocsSourceConfiguration.GetContentCacheKey(DocsSourceConfiguration.GetLlmsTxtUrl(configuration));
    private readonly ILogger<DocsFetcher> _logger = logger;

    public async Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
    {
        await MigrateLegacyCacheAsync(cancellationToken).ConfigureAwait(false);
        return await CachedHttpDocumentFetcher.FetchAsync(_httpClient, _cache, _llmsTxtUrl, _cacheKey, _logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateLegacyCacheAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(_cacheKey, _llmsTxtUrl, StringComparison.Ordinal))
        {
            return;
        }

        var currentContent = await _cache.GetAsync(_cacheKey, cancellationToken).ConfigureAwait(false);
        var currentETag = await _cache.GetETagAsync(_cacheKey, cancellationToken).ConfigureAwait(false);
        if (currentContent is not null || currentETag is not null)
        {
            await ClearLegacyCacheAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var legacyContent = await _cache.GetAsync(_llmsTxtUrl, cancellationToken).ConfigureAwait(false);
        var legacyETag = await _cache.GetETagAsync(_llmsTxtUrl, cancellationToken).ConfigureAwait(false);

        if (legacyContent is not null)
        {
            await _cache.SetAsync(_cacheKey, legacyContent, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(legacyETag))
        {
            await _cache.SetETagAsync(_cacheKey, legacyETag, cancellationToken).ConfigureAwait(false);
        }

        if (legacyContent is not null || !string.IsNullOrEmpty(legacyETag))
        {
            await ClearLegacyCacheAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ClearLegacyCacheAsync(CancellationToken cancellationToken)
    {
        await _cache.InvalidateAsync(_llmsTxtUrl, cancellationToken).ConfigureAwait(false);
        await _cache.SetETagAsync(_llmsTxtUrl, null, cancellationToken).ConfigureAwait(false);
    }
}
