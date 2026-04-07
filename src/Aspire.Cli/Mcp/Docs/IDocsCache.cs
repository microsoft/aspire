// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Mcp.Docs;

/// <summary>
/// Interface for caching aspire.dev documentation content with ETag support.
/// </summary>
internal interface IDocsCache
{
    /// <summary>
    /// Gets cached documentation content by key.
    /// </summary>
    /// <param name="key">The cache key (e.g., URL or topic identifier).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached content, or null if not found.</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets documentation content in the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="content">The content to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetAsync(string key, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the cached ETag for a URL.
    /// </summary>
    /// <param name="url">The URL to get the ETag for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached ETag, or null if not found.</returns>
    Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears the ETag for a URL.
    /// </summary>
    /// <param name="url">The URL to set the ETag for.</param>
    /// <param name="etag">The ETag value, or null to clear the cached ETag.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the cached parsed document index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached documents, or null if not found.</returns>
    Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the parsed document index in the cache.
    /// </summary>
    /// <param name="documents">The parsed documents to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the cache for a key.
    /// </summary>
    /// <param name="key">The cache key to invalidate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task InvalidateAsync(string key, CancellationToken cancellationToken = default);
}
