// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation;

/// <summary>
/// Provides conditional HTTP GETs backed by an <see cref="IDocumentContentCache"/>.
/// </summary>
internal static class CachedHttpDocumentFetcher
{
    /// <summary>
    /// Fetches a document with conditional requests and cache fallback behavior.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to fetch the document.</param>
    /// <param name="cache">The cache used for content and ETag persistence.</param>
    /// <param name="url">The document URL.</param>
    /// <param name="cacheKey">The cache key used for persisted content and ETag state, or <c>null</c> to use the URL.</param>
    /// <param name="logger">The logger used for diagnostics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The fetched or cached content, or <c>null</c> when unavailable.</returns>
    public static async Task<string?> FetchAsync(
        HttpClient httpClient,
        IDocumentContentCache cache,
        string url,
        string? cacheKey,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var storageKey = string.IsNullOrWhiteSpace(cacheKey) ? url : cacheKey;

        try
        {
            var cachedETag = await cache.GetETagAsync(storageKey, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cachedETag))
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cachedETag));
            }

            logger.LogDebug("Fetching content from {Url}, cached ETag: {ETag}", url, cachedETag ?? "(none)");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response is { StatusCode: HttpStatusCode.NotModified })
            {
                logger.LogDebug("Server returned 304 Not Modified for {Url}", url);

                var cached = await cache.GetAsync(storageKey, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
                }

                logger.LogDebug("Cached ETag exists but cached content is missing for {Url}; clearing ETag and retrying", url);
                await cache.SetETagAsync(storageKey, null, cancellationToken).ConfigureAwait(false);

                using var retryResponse = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                retryResponse.EnsureSuccessStatusCode();

                var retryContent = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var retryETag = retryResponse.Headers.ETag?.Tag;
                if (!string.IsNullOrEmpty(retryETag))
                {
                    await cache.SetETagAsync(storageKey, retryETag, cancellationToken).ConfigureAwait(false);
                }

                await cache.SetAsync(storageKey, retryContent, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Fetched content from {Url} after retry, length: {Length} chars", url, retryContent.Length);
                return retryContent;
            }

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var newETag = response.Headers.ETag?.Tag;
            if (!string.IsNullOrEmpty(newETag))
            {
                await cache.SetETagAsync(storageKey, newETag, cancellationToken).ConfigureAwait(false);
                logger.LogDebug("Stored new ETag for {Url}: {ETag}", url, newETag);
            }

            await cache.SetAsync(storageKey, content, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Fetched content from {Url}, length: {Length} chars", url, content.Length);
            return content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch content from {Url}", url);

            var cached = await cache.GetAsync(storageKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                logger.LogDebug("Returning cached content for {Url} after fetch failure", url);
                return cached;
            }

            return null;
        }
    }

    public static Task<string?> FetchAsync(
        HttpClient httpClient,
        IDocumentContentCache cache,
        string url,
        ILogger logger,
        CancellationToken cancellationToken = default)
        => FetchAsync(httpClient, cache, url, cacheKey: null, logger, cancellationToken);
}
