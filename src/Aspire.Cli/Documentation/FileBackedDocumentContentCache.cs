// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation;

/// <summary>
/// Provides file-backed cached text and JSON content with ETag support.
/// </summary>
internal sealed class FileBackedDocumentContentCache(
    IMemoryCache memoryCache,
    CliExecutionContext executionContext,
    string subdirectory,
    ILogger logger)
{
    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger _logger = logger;
    private readonly DirectoryInfo _diskCacheDirectory = new(Path.Combine(executionContext.CacheDirectory.FullName, subdirectory));

    /// <summary>
    /// Gets cached text content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached content, or <c>null</c> if it is not available.</returns>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetMemoryCacheKey("text", key);
        if (_memoryCache.TryGetValue(cacheKey, out string? content))
        {
            _logger.LogDebug("Content cache memory hit for key: {Key}", key);
            return content;
        }

        var diskContent = await ReadTextFileAsync(GetTextFilePath(key), cancellationToken).ConfigureAwait(false);
        if (diskContent is not null)
        {
            _memoryCache.Set(cacheKey, diskContent);
            _logger.LogDebug("Content cache disk hit for key: {Key}", key);
            return diskContent;
        }

        _logger.LogDebug("Content cache miss for key: {Key}", key);
        return null;
    }

    /// <summary>
    /// Stores text content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="content">The content to store.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _memoryCache.Set(GetMemoryCacheKey("text", key), content);
        await WriteTextFileAsync(GetTextFilePath(key), content, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Content cache set for key: {Key}", key);
    }

    /// <summary>
    /// Gets the cached ETag for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached ETag, or <c>null</c> if it is not available.</returns>
    public async Task<string?> GetETagAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetMemoryCacheKey("etag", key);
        if (_memoryCache.TryGetValue(cacheKey, out string? etag))
        {
            _logger.LogDebug("ETag cache memory hit for key: {Key}", key);
            return etag;
        }

        var diskETag = await ReadTextFileAsync(GetETagFilePath(key), cancellationToken).ConfigureAwait(false);
        if (diskETag is not null)
        {
            var trimmed = diskETag.Trim();
            _memoryCache.Set(cacheKey, trimmed);
            _logger.LogDebug("ETag cache disk hit for key: {Key}", key);
            return trimmed;
        }

        _logger.LogDebug("ETag cache miss for key: {Key}", key);
        return null;
    }

    /// <summary>
    /// Stores or clears the cached ETag for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="etag">The ETag to store, or <c>null</c> to clear it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task SetETagAsync(string key, string? etag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = GetMemoryCacheKey("etag", key);
        if (etag is null)
        {
            _memoryCache.Remove(cacheKey);
            DeleteFileQuietly(GetETagFilePath(key));
            _logger.LogDebug("ETag cache cleared for key: {Key}", key);
            return;
        }

        _memoryCache.Set(cacheKey, etag);
        await WriteTextFileAsync(GetETagFilePath(key), etag, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ETag cache set for key: {Key}", key);
    }

    /// <summary>
    /// Removes cached text content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _memoryCache.Remove(GetMemoryCacheKey("text", key));
        DeleteFileQuietly(GetTextFilePath(key));
        _logger.LogDebug("Content cache invalidated key: {Key}", key);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes cached JSON content for the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task InvalidateJsonAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _memoryCache.Remove(GetMemoryCacheKey("json", key));
        DeleteFileQuietly(GetJsonFilePath(key));
        _logger.LogDebug("JSON cache invalidated key: {Key}", key);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets cached JSON content for the specified key.
    /// </summary>
    /// <typeparam name="T">The JSON payload type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="jsonTypeInfo">The source-generated JSON metadata.</param>
    /// <param name="requiredETagKey">An optional key whose ETag must still exist for the cached JSON to remain valid.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached JSON value, or <c>default</c> when it is not available.</returns>
    public async Task<T?> GetJsonAsync<T>(
        string key,
        JsonTypeInfo<T> jsonTypeInfo,
        string? requiredETagKey = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(requiredETagKey))
        {
            var etag = await GetETagAsync(requiredETagKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(etag))
            {
                _memoryCache.Remove(GetMemoryCacheKey("json", key));
                DeleteFileQuietly(GetJsonFilePath(key));
                return default;
            }
        }

        var cacheKey = GetMemoryCacheKey("json", key);
        if (_memoryCache.TryGetValue(cacheKey, out T? cached))
        {
            _logger.LogDebug("JSON cache memory hit for key: {Key}", key);
            return cached;
        }

        try
        {
            var filePath = GetJsonFilePath(key);
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("JSON cache miss for key: {Key}", key);
                return default;
            }

            await using var stream = File.OpenRead(filePath);
            var value = await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                _memoryCache.Set(cacheKey, value);
                _logger.LogDebug("JSON cache disk hit for key: {Key}", key);
            }

            return value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read JSON cache for key: {Key}", key);
            return default;
        }
    }

    /// <summary>
    /// Stores JSON content for the specified key.
    /// </summary>
    /// <typeparam name="T">The JSON payload type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="jsonTypeInfo">The source-generated JSON metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task SetJsonAsync<T>(
        string key,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _memoryCache.Set(GetMemoryCacheKey("json", key), value);

        try
        {
            EnsureCacheDirectoryExists();

            var filePath = GetJsonFilePath(key);
            var tempPath = filePath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, jsonTypeInfo, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, filePath, overwrite: true);
            _logger.LogDebug("JSON cache set for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write JSON cache for key: {Key}", key);
        }
    }

    private string GetTextFilePath(string key) => Path.Combine(_diskCacheDirectory.FullName, $"{SanitizeFileName(key)}.txt");

    private string GetETagFilePath(string key) => Path.Combine(_diskCacheDirectory.FullName, $"{SanitizeFileName(key)}.etag.txt");

    private string GetJsonFilePath(string key) => Path.Combine(_diskCacheDirectory.FullName, $"{SanitizeFileName(key)}.json");

    private string GetMemoryCacheKey(string kind, string key) => $"{_diskCacheDirectory.Name}:{kind}:{key}";

    private static string SanitizeFileName(string key)
    {
        var result = new char[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            result[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }

        return new string(result);
    }

    private async Task<string?> ReadTextFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read cache file: {FilePath}", filePath);
            return null;
        }
    }

    private async Task WriteTextFileAsync(string filePath, string content, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCacheDirectoryExists();

            var tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write cache file: {FilePath}", filePath);
        }
    }

    private void EnsureCacheDirectoryExists()
    {
        if (_diskCacheDirectory.Exists)
        {
            return;
        }

        try
        {
            _diskCacheDirectory.Create();
            _diskCacheDirectory.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create cache directory: {Directory}", _diskCacheDirectory.FullName);
        }
    }

    private void DeleteFileQuietly(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete cache file: {FilePath}", filePath);
        }
    }
}
