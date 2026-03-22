// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Mcp.Docs;

/// <summary>
/// Cache for aspire.dev documentation content with ETag support.
/// Uses both in-memory cache for fast access and disk cache for persistence across CLI invocations.
/// </summary>
internal sealed class DocsCache : IDocsCache
{
    private const string DocsCacheSubdirectory = "docs";
    private const string ETagFileName = "etag.txt";
    private const string IndexFileName = "index.json";
    private const string IndexCacheKey = "docs:index";

    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<DocsCache> _logger;
    private readonly DirectoryInfo _diskCacheDirectory;

    public DocsCache(IMemoryCache memoryCache, CliExecutionContext executionContext, ILogger<DocsCache> logger)
    {
        _memoryCache = memoryCache;
        _logger = logger;
        _diskCacheDirectory = new DirectoryInfo(Path.Combine(executionContext.CacheDirectory.FullName, DocsCacheSubdirectory));
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetCacheKey(key);

        // Check memory cache first
        if (_memoryCache.TryGetValue(cacheKey, out string? content))
        {
            _logger.LogDebug("DocsCache memory hit for key: {Key}", key);
            return content;
        }

        // Check disk cache
        var diskContent = await GetFromDiskAsync(key, cancellationToken).ConfigureAwait(false);
        if (diskContent is not null)
        {
            // Populate memory cache from disk
            _memoryCache.Set(cacheKey, diskContent);
            _logger.LogDebug("DocsCache disk hit for key: {Key}", key);
            return diskContent;
        }

        _logger.LogDebug("DocsCache miss for key: {Key}", key);
        return null;
    }

    public async Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetCacheKey(key);

        // Set in memory cache
        _memoryCache.Set(cacheKey, content);

        // Persist to disk
        await SaveToDiskAsync(key, content, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("DocsCache set for key: {Key}", key);
    }

    public async Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetETagCacheKey(url);

        // Check memory cache first
        if (_memoryCache.TryGetValue(cacheKey, out string? etag))
        {
            _logger.LogDebug("DocsCache ETag memory hit for url: {Url}", url);
            return etag;
        }

        // Check disk cache
        var diskETag = await GetETagFromDiskAsync(cancellationToken).ConfigureAwait(false);
        if (diskETag is not null)
        {
            // Populate memory cache from disk
            _memoryCache.Set(cacheKey, diskETag);
            _logger.LogDebug("DocsCache ETag disk hit for url: {Url}", url);
            return diskETag;
        }

        _logger.LogDebug("DocsCache ETag miss for url: {Url}", url);
        return null;
    }

    public async Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetETagCacheKey(url);

        if (etag is null)
        {
            _memoryCache.Remove(cacheKey);
            await DeleteETagFromDiskAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("DocsCache cleared ETag for url: {Url}", url);
        }
        else
        {
            _memoryCache.Set(cacheKey, etag);
            await SaveETagToDiskAsync(etag, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("DocsCache set ETag for url: {Url}, ETag: {ETag}", url, etag);
        }
    }

    public async Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check memory cache first
        if (_memoryCache.TryGetValue(IndexCacheKey, out LlmsDocument[]? documents))
        {
            _logger.LogDebug("DocsCache index memory hit");
            return documents;
        }

        // Check disk cache
        var diskDocuments = await GetIndexFromDiskAsync(cancellationToken).ConfigureAwait(false);
        if (diskDocuments is not null)
        {
            // Populate memory cache from disk
            _memoryCache.Set(IndexCacheKey, diskDocuments);
            _logger.LogDebug("DocsCache index disk hit, loaded {Count} documents", diskDocuments.Length);
            return diskDocuments;
        }

        _logger.LogDebug("DocsCache index miss");
        return null;
    }

    public async Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Set in memory cache
        _memoryCache.Set(IndexCacheKey, documents);

        // Persist to disk
        await SaveIndexToDiskAsync(documents, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("DocsCache set index with {Count} documents", documents.Length);
    }

    public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetCacheKey(key);
        _memoryCache.Remove(cacheKey);

        // Also invalidate disk cache
        try
        {
            var contentFile = GetContentFilePath(key);
            if (File.Exists(contentFile))
            {
                File.Delete(contentFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete disk cache for key: {Key}", key);
        }

        _logger.LogDebug("DocsCache invalidated key: {Key}", key);
        return Task.CompletedTask;
    }

    private static string GetCacheKey(string key) => $"docs:{key}";

    private static string GetETagCacheKey(string url) => $"docs:etag:{url}";

    private string GetContentFilePath(string key)
    {
        // Use a simple sanitized filename for the key
        var safeKey = SanitizeFileName(key);
        return Path.Combine(_diskCacheDirectory.FullName, $"{safeKey}.txt");
    }

    private string GetETagFilePath() => Path.Combine(_diskCacheDirectory.FullName, ETagFileName);

    private static readonly char[] s_invalidFileNameChars = Path.GetInvalidFileNameChars();

    private static string SanitizeFileName(string key)
    {
        // Replace invalid filename characters with underscore
        var result = new char[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            result[i] = Array.IndexOf(s_invalidFileNameChars, c) >= 0 ? '_' : c;
        }
        return new string(result);
    }

    private async Task<string?> GetFromDiskAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var filePath = GetContentFilePath(key);
            if (File.Exists(filePath))
            {
                return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read disk cache for key: {Key}", key);
        }

        return null;
    }

    private async Task SaveToDiskAsync(string key, string content, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCacheDirectoryExists();

            var filePath = GetContentFilePath(key);
            var tempPath = filePath + ".tmp";

            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

            // Atomic move (overwrite: true uses atomic rename on supported platforms)
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save disk cache for key: {Key}", key);
        }
    }

    private async Task<string?> GetETagFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filePath = GetETagFilePath();
            if (File.Exists(filePath))
            {
                return (await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false)).Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read ETag from disk");
        }

        return null;
    }

    private async Task SaveETagToDiskAsync(string etag, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCacheDirectoryExists();

            var filePath = GetETagFilePath();
            await File.WriteAllTextAsync(filePath, etag, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save ETag to disk");
        }
    }

    private Task DeleteETagFromDiskAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        try
        {
            var filePath = GetETagFilePath();
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete ETag from disk");
        }

        return Task.CompletedTask;
    }

    private void EnsureCacheDirectoryExists()
    {
        if (!_diskCacheDirectory.Exists)
        {
            try
            {
                _diskCacheDirectory.Create();
                _diskCacheDirectory.Refresh();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create docs cache directory: {Directory}", _diskCacheDirectory.FullName);
            }
        }
    }

    private string GetIndexFilePath() => Path.Combine(_diskCacheDirectory.FullName, IndexFileName);

    private async Task<LlmsDocument[]?> GetIndexFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filePath = GetIndexFilePath();
            var etagFilePath = GetETagFilePath();
            
            // Only return cached index if ETag also exists (ensures consistency)
            if (File.Exists(filePath) && File.Exists(etagFilePath))
            {
                await using var stream = File.OpenRead(filePath);
                return await JsonSerializer.DeserializeAsync(stream, JsonSourceGenerationContext.Default.LlmsDocumentArray, cancellationToken).ConfigureAwait(false);
            }
            
            // If index exists but ETag is missing, delete the stale index
            if (File.Exists(filePath) && !File.Exists(etagFilePath))
            {
                _logger.LogDebug("Deleting stale index (ETag missing)");
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete stale index");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read index from disk");
        }

        return null;
    }

    private async Task SaveIndexToDiskAsync(LlmsDocument[] documents, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCacheDirectoryExists();

            var filePath = GetIndexFilePath();
            var tempPath = filePath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, documents, JsonSourceGenerationContext.Default.LlmsDocumentArray, cancellationToken).ConfigureAwait(false);
            }

            // Atomic move
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save index to disk");
        }
    }
}
