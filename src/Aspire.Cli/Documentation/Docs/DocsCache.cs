// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.Docs;

/// <summary>
/// Cache for aspire.dev documentation content with ETag support.
/// Uses both in-memory cache for fast access and disk cache for persistence across CLI invocations.
/// </summary>
internal sealed class DocsCache : IDocsCache
{
    private const string DocsCacheSubdirectory = "docs";
    private const string IndexCacheKey = "index";

    private readonly FileBackedDocumentContentCache _contentCache;
    private readonly string _llmsTxtUrl;

    public DocsCache(
        IMemoryCache memoryCache,
        CliExecutionContext executionContext,
        IConfiguration configuration,
        ILogger<DocsCache> logger)
    {
        _llmsTxtUrl = DocsSourceConfiguration.GetLlmsTxtUrl(configuration);
        _contentCache = new FileBackedDocumentContentCache(memoryCache, executionContext, DocsCacheSubdirectory, logger);
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => _contentCache.GetAsync(key, cancellationToken);

    public Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
        => _contentCache.SetAsync(key, content, cancellationToken);

    public Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default)
        => _contentCache.GetETagAsync(url, cancellationToken);

    public Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default)
        => _contentCache.SetETagAsync(url, etag, cancellationToken);

    public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
        => _contentCache.InvalidateAsync(key, cancellationToken);

    public Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default)
        => _contentCache.GetJsonAsync(IndexCacheKey, JsonSourceGenerationContext.Default.LlmsDocumentArray, _llmsTxtUrl, cancellationToken);

    public Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default)
        => _contentCache.SetJsonAsync(IndexCacheKey, documents, JsonSourceGenerationContext.Default.LlmsDocumentArray, cancellationToken);
}
