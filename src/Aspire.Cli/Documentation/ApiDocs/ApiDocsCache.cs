// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Cache for Aspire API documentation content and the parsed API index.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ApiDocsCache"/> class.
/// </remarks>
/// <param name="memoryCache">The in-memory cache.</param>
/// <param name="executionContext">The CLI execution context.</param>
/// <param name="configuration">The configuration used to resolve API docs source URLs.</param>
/// <param name="logger">The logger.</param>
internal sealed class ApiDocsCache(
    IMemoryCache memoryCache,
    CliExecutionContext executionContext,
    IConfiguration configuration,
    ILogger<ApiDocsCache> logger) : IApiDocsCache
{
    private const string ApiDocsCacheSubdirectory = "api-docs";
    private const string IndexCacheKey = "index";

    private readonly FileBackedDocumentContentCache _contentCache = new(memoryCache, executionContext, ApiDocsCacheSubdirectory, logger);
    private readonly string _sitemapUrl = ApiDocsSourceConfiguration.GetSitemapUrl(configuration);

    /// <summary>
    /// Gets cached content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached content, or <c>null</c> if it is not available.</returns>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => _contentCache.GetAsync(key, cancellationToken);

    /// <summary>
    /// Stores content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="content">The content to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
        => _contentCache.SetAsync(key, content, cancellationToken);

    /// <summary>
    /// Gets the cached ETag for the specified URL.
    /// </summary>
    /// <param name="url">The URL key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached ETag, or <c>null</c> if it is not available.</returns>
    public Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default)
        => _contentCache.GetETagAsync(url, cancellationToken);

    /// <summary>
    /// Stores or clears the cached ETag for the specified URL.
    /// </summary>
    /// <param name="url">The URL key.</param>
    /// <param name="etag">The ETag to cache, or <c>null</c> to clear it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default)
        => _contentCache.SetETagAsync(url, etag, cancellationToken);

    /// <summary>
    /// Invalidates cached content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
        => _contentCache.InvalidateAsync(key, cancellationToken);

    /// <summary>
    /// Gets the cached API reference index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached index, or <c>null</c> if it is not available.</returns>
    public Task<ApiReferenceItem[]?> GetIndexAsync(CancellationToken cancellationToken = default)
        => _contentCache.GetJsonAsync(IndexCacheKey, JsonSourceGenerationContext.Default.ApiReferenceItemArray, _sitemapUrl, cancellationToken);

    /// <summary>
    /// Stores the API reference index in the cache.
    /// </summary>
    /// <param name="documents">The items to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task SetIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
        => _contentCache.SetJsonAsync(IndexCacheKey, documents, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken);
}
