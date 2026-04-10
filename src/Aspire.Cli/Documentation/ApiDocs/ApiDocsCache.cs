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

    private readonly FileBackedDocumentContentCache _contentCache = new(memoryCache, executionContext, ApiDocsCacheSubdirectory, logger);
    private readonly string _indexCacheKey = ApiDocsSourceConfiguration.GetIndexCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration));
    private readonly string _indexSourceFingerprintCacheKey = $"{ApiDocsSourceConfiguration.GetIndexCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration))}:fingerprint";
    private readonly string _memberIndexCacheKey = ApiDocsSourceConfiguration.GetMemberIndexCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration));
    private readonly string _memberIndexSourceFingerprintCacheKey = $"{ApiDocsSourceConfiguration.GetMemberIndexCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration))}:fingerprint";
    private readonly string _legacyIndexCacheKey = ApiDocsSourceConfiguration.GetLegacyIndexCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration));
    private readonly string _legacyIndexSourceFingerprintCacheKey = $"{ApiDocsSourceConfiguration.GetLegacyIndexCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration))}:fingerprint";

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
    public async Task<ApiReferenceItem[]?> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        var documents = await _contentCache.GetJsonAsync(_indexCacheKey, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (documents is not null)
        {
            await MigrateLegacyIndexFingerprintAsync(cancellationToken).ConfigureAwait(false);
            await ClearLegacyIndexCacheAsync(cancellationToken).ConfigureAwait(false);
            return documents;
        }

        if (!HasLegacyIndexCacheKey)
        {
            return null;
        }

        var legacyDocuments = await _contentCache.GetJsonAsync(_legacyIndexCacheKey, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (legacyDocuments is null)
        {
            return null;
        }

        await _contentCache.SetJsonAsync(_indexCacheKey, legacyDocuments, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken).ConfigureAwait(false);
        await MigrateLegacyIndexFingerprintAsync(cancellationToken).ConfigureAwait(false);
        await ClearLegacyIndexCacheAsync(cancellationToken).ConfigureAwait(false);
        return legacyDocuments;
    }

    /// <summary>
    /// Stores the API reference index in the cache.
    /// </summary>
    /// <param name="documents">The items to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task SetIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
    {
        await _contentCache.SetJsonAsync(_indexCacheKey, documents, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken).ConfigureAwait(false);
        await ClearLegacyIndexCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the fingerprint for the sitemap content used to build the cached index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached sitemap fingerprint, or <c>null</c> if it is not available.</returns>
    public async Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var fingerprint = await _contentCache.GetAsync(_indexSourceFingerprintCacheKey, cancellationToken).ConfigureAwait(false);
        if (fingerprint is not null)
        {
            await ClearLegacyIndexCacheAsync(cancellationToken).ConfigureAwait(false);
            return fingerprint;
        }

        if (!HasLegacyIndexCacheKey)
        {
            return null;
        }

        var legacyFingerprint = await _contentCache.GetAsync(_legacyIndexSourceFingerprintCacheKey, cancellationToken).ConfigureAwait(false);
        if (legacyFingerprint is null)
        {
            return null;
        }

        await _contentCache.SetAsync(_indexSourceFingerprintCacheKey, legacyFingerprint, cancellationToken).ConfigureAwait(false);
        await ClearLegacyIndexCacheAsync(cancellationToken).ConfigureAwait(false);
        return legacyFingerprint;
    }

    /// <summary>
    /// Stores the fingerprint for the sitemap content used to build the cached index.
    /// </summary>
    /// <param name="fingerprint">The sitemap fingerprint.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        await _contentCache.SetAsync(_indexSourceFingerprintCacheKey, fingerprint, cancellationToken).ConfigureAwait(false);
        await ClearLegacyIndexCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the cached member index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached member index, or <c>null</c> if it is not available.</returns>
    public Task<ApiReferenceItem[]?> GetMemberIndexAsync(CancellationToken cancellationToken = default)
        => _contentCache.GetJsonAsync(_memberIndexCacheKey, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken: cancellationToken);

    /// <summary>
    /// Stores the member index in the cache.
    /// </summary>
    /// <param name="documents">The items to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task SetMemberIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
        => _contentCache.SetJsonAsync(_memberIndexCacheKey, documents, JsonSourceGenerationContext.Default.ApiReferenceItemArray, cancellationToken);

    /// <summary>
    /// Gets the fingerprint for the sitemap content used to build the cached member index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached sitemap fingerprint, or <c>null</c> if it is not available.</returns>
    public Task<string?> GetMemberIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
        => _contentCache.GetAsync(_memberIndexSourceFingerprintCacheKey, cancellationToken);

    /// <summary>
    /// Stores the fingerprint for the sitemap content used to build the cached member index.
    /// </summary>
    /// <param name="fingerprint">The sitemap fingerprint.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task SetMemberIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
        => _contentCache.SetAsync(_memberIndexSourceFingerprintCacheKey, fingerprint, cancellationToken);

    private bool HasLegacyIndexCacheKey => !string.Equals(_legacyIndexCacheKey, _indexCacheKey, StringComparison.Ordinal);

    private async Task MigrateLegacyIndexFingerprintAsync(CancellationToken cancellationToken)
    {
        if (!HasLegacyIndexCacheKey)
        {
            return;
        }

        var currentFingerprint = await _contentCache.GetAsync(_indexSourceFingerprintCacheKey, cancellationToken).ConfigureAwait(false);
        if (currentFingerprint is not null)
        {
            return;
        }

        var legacyFingerprint = await _contentCache.GetAsync(_legacyIndexSourceFingerprintCacheKey, cancellationToken).ConfigureAwait(false);
        if (legacyFingerprint is not null)
        {
            await _contentCache.SetAsync(_indexSourceFingerprintCacheKey, legacyFingerprint, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ClearLegacyIndexCacheAsync(CancellationToken cancellationToken)
    {
        if (!HasLegacyIndexCacheKey)
        {
            return;
        }

        await _contentCache.InvalidateJsonAsync(_legacyIndexCacheKey, cancellationToken).ConfigureAwait(false);
        await _contentCache.InvalidateAsync(_legacyIndexSourceFingerprintCacheKey, cancellationToken).ConfigureAwait(false);
    }
}
