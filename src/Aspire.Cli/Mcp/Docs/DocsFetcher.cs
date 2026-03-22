// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Mcp.Docs;

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
internal sealed class DocsFetcher(HttpClient httpClient, IDocsCache cache, ILogger<DocsFetcher> logger) : IDocsFetcher
{
    private const string SmallDocsUrl = "https://aspire.dev/llms-small.txt";

    private readonly HttpClient _httpClient = httpClient;
    private readonly IDocsCache _cache = cache;
    private readonly ILogger<DocsFetcher> _logger = logger;

    public async Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Get cached ETag for conditional request
            var cachedETag = await _cache.GetETagAsync(SmallDocsUrl, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, SmallDocsUrl);

            // Add If-None-Match header if we have a cached ETag
            if (!string.IsNullOrEmpty(cachedETag))
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cachedETag));
            }

            _logger.LogDebug("Fetching aspire.dev docs from {Url}, cached ETag: {ETag}", SmallDocsUrl, cachedETag ?? "(none)");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // If not modified, return cached content
            if (response is { StatusCode: HttpStatusCode.NotModified })
            {
                _logger.LogDebug("Server returned 304 Not Modified, using cached content");

                var cached = await _cache.GetAsync(SmallDocsUrl, cancellationToken).ConfigureAwait(false);

                if (cached is not null)
                {
                    return cached;
                }

                // Cache was cleared but ETag still exists - clear ETag and retry without If-None-Match
                _logger.LogDebug("Cache content missing despite valid ETag, clearing ETag and retrying");
                await _cache.SetETagAsync(SmallDocsUrl, null, cancellationToken).ConfigureAwait(false);

                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, SmallDocsUrl);
                using var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);

                retryResponse.EnsureSuccessStatusCode();

                var retryContent = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                // Store the new ETag if present
                var retryETag = retryResponse.Headers.ETag?.Tag;
                if (!string.IsNullOrEmpty(retryETag))
                {
                    await _cache.SetETagAsync(SmallDocsUrl, retryETag, cancellationToken).ConfigureAwait(false);

                    _logger.LogDebug("Stored new ETag after retry: {ETag}", retryETag);
                }

                // Cache the content
                await _cache.SetAsync(SmallDocsUrl, retryContent, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Fetched aspire.dev docs after retry, length: {Length} chars", retryContent.Length);

                return retryContent;
            }

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Store the new ETag if present
            var newETag = response.Headers.ETag?.Tag;
            if (!string.IsNullOrEmpty(newETag))
            {
                await _cache.SetETagAsync(SmallDocsUrl, newETag, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Stored new ETag: {ETag}", newETag);
            }

            // Cache the content
            await _cache.SetAsync(SmallDocsUrl, content, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Fetched aspire.dev docs, length: {Length} chars", content.Length);

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch aspire.dev docs");

            // Try to return cached content on error
            var cached = await _cache.GetAsync(SmallDocsUrl, cancellationToken).ConfigureAwait(false);

            if (cached is not null)
            {
                _logger.LogDebug("Returning cached content after fetch failure");

                return cached;
            }

            return null;
        }
    }
}
