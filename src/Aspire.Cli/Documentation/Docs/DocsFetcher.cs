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
    private readonly ILogger<DocsFetcher> _logger = logger;

    public async Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
    {
        return await CachedHttpDocumentFetcher.FetchAsync(_httpClient, _cache, _llmsTxtUrl, _logger, cancellationToken).ConfigureAwait(false);
    }
}
