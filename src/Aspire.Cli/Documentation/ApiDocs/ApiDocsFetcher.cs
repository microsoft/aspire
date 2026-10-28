// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Service for fetching Aspire API reference content.
/// </summary>
internal interface IApiDocsFetcher
{
    /// <summary>
    /// Fetches the sitemap used to build the API catalog.
    /// </summary>
    Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches markdown content for the given API page URL.
    /// </summary>
    Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IApiDocsFetcher"/> with cache-backed ETag support.
/// </summary>
internal sealed class ApiDocsFetcher(HttpClient httpClient, IApiDocsCache cache, IConfiguration configuration, ILogger<ApiDocsFetcher> logger) : IApiDocsFetcher
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IApiDocsCache _cache = cache;
    private readonly string _sitemapUrl = ApiDocsSourceConfiguration.GetSitemapUrl(configuration);
    private readonly string _sitemapCacheKey = ApiDocsSourceConfiguration.GetSitemapContentCacheKey(ApiDocsSourceConfiguration.GetSitemapUrl(configuration));
    private readonly ILogger<ApiDocsFetcher> _logger = logger;

    /// <summary>
    /// Fetches the sitemap used to build the API catalog.
    /// </summary>
    /// <remarks>
    /// When the configured URL is a sitemap index, every child sitemap is fetched and merged into a single
    /// <c>urlset</c> document so callers can treat both shapes identically.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sitemap content, or <c>null</c> when it cannot be retrieved.</returns>
    public async Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default)
    {
        var rootContent = await CachedHttpDocumentFetcher.FetchAsync(_httpClient, _cache, _sitemapUrl, _sitemapCacheKey, _logger, cancellationToken).ConfigureAwait(false);
        if (rootContent is null || !TryReadSitemapIndexChildren(rootContent, out var childUrls))
        {
            return rootContent;
        }

        if (childUrls.Count is 0)
        {
            _logger.LogWarning("The API sitemap index at {SitemapUrl} does not list any child sitemaps", _sitemapUrl);
            return null;
        }

        // Child <loc> values are canonical (https://aspire.dev/...). Rebase them so a staging or local
        // sitemap override never silently reads production's child sitemaps.
        var rebasedChildUrls = childUrls
            .Select(childUrl => ApiDocsSourceConfiguration.RebasePageUrl(childUrl, _sitemapUrl))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var childContents = await Task.WhenAll(rebasedChildUrls.Select(childUrl => CachedHttpDocumentFetcher.FetchAsync(
            _httpClient,
            _cache,
            childUrl,
            ApiDocsSourceConfiguration.GetSitemapContentCacheKey(childUrl),
            _logger,
            cancellationToken))).ConfigureAwait(false);

        // A missing child (with no cached copy) would silently drop a whole language from the catalog,
        // so treat it as a failed refresh and let the caller fall back to its cached index.
        for (var i = 0; i < childContents.Length; i++)
        {
            if (childContents[i] is null)
            {
                _logger.LogWarning("Failed to fetch API child sitemap {SitemapUrl}; the API catalog is incomplete", rebasedChildUrls[i]);
                return null;
            }
        }

        return MergeSitemaps(rebasedChildUrls, childContents!);
    }

    /// <summary>
    /// Fetches markdown content for the specified API page.
    /// </summary>
    /// <param name="pageUrl">The canonical API page URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The markdown content, or <c>null</c> when it cannot be retrieved.</returns>
    public Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        var markdownUrl = ApiDocsSourceConfiguration.BuildMarkdownUrl(pageUrl, _sitemapUrl);
        var cacheKey = ApiDocsSourceConfiguration.GetPageContentCacheKey(pageUrl, _sitemapUrl);
        return CachedHttpDocumentFetcher.FetchAsync(_httpClient, _cache, markdownUrl, cacheKey, _logger, cancellationToken);
    }

    private static bool TryReadSitemapIndexChildren(string content, out List<string> childUrls)
    {
        childUrls = [];

        // A sitemap index looks like:
        //   <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
        //     <sitemap><loc>https://aspire.dev/sitemap-0.xml</loc></sitemap>
        //     <sitemap><loc>https://aspire.dev/sitemap-api.xml</loc></sitemap>
        //   </sitemapindex>
        // Anything else (a plain <urlset>, or content that is not XML) is returned untouched so the
        // existing parsing and error behavior is preserved.
        try
        {
            using var reader = CreateXmlReader(content);
            if (reader.MoveToContent() is not XmlNodeType.Element || reader.LocalName is not "sitemapindex")
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Element && reader.LocalName is "loc")
                {
                    var location = reader.ReadElementContentAsString().Trim();
                    if (location.Length > 0)
                    {
                        childUrls.Add(location);
                    }
                }
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private string? MergeSitemaps(string[] sitemapUrls, string[] sitemapContents)
    {
        // Union the entries across children and sort them so the merged document, and therefore the index
        // fingerprint, is stable regardless of child order. A page can legitimately appear in more than one child.
        var locations = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sitemapContents.Length; i++)
        {
            try
            {
                using var reader = CreateXmlReader(sitemapContents[i]);
                while (reader.Read())
                {
                    if (reader.NodeType is XmlNodeType.Element && reader.LocalName is "loc")
                    {
                        var location = reader.ReadElementContentAsString().Trim();
                        if (location.Length > 0)
                        {
                            locations.Add(location);
                        }
                    }
                }
            }
            catch (XmlException ex)
            {
                // A child can answer 200 with a body that is not well-formed XML, such as a truncated download
                // or an HTML error page. Treat it like a missing child so the caller falls back to its cached
                // index instead of failing the command.
                _logger.LogWarning(ex, "API child sitemap {SitemapUrl} is not well-formed XML; the API catalog is incomplete", sitemapUrls[i]);
                return null;
            }
        }

        var merged = new StringBuilder();
        using (var writer = XmlWriter.Create(merged, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach (var location in locations)
            {
                writer.WriteStartElement("url");
                writer.WriteElementString("loc", location);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        return merged.ToString();
    }

    private static XmlReader CreateXmlReader(string content)
        => XmlReader.Create(new StringReader(content), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });
}
