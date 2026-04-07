// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Resolves configuration for Aspire API docs sources.
/// </summary>
internal static class ApiDocsSourceConfiguration
{
    private const string IndexCacheKeyPrefix = "index:";

    /// <summary>
    /// Configuration key for overriding the API sitemap URL.
    /// </summary>
    public const string SitemapUrlConfigKey = "Aspire:Cli:ApiDocs:SitemapUrl";

    /// <summary>
    /// Default sitemap URL for Aspire API reference pages.
    /// </summary>
    public const string DefaultSitemapUrl = "https://aspire.dev/sitemap-0.xml";

    /// <summary>
    /// Gets the sitemap URL used to build the API index.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <returns>The resolved sitemap URL.</returns>
    public static string GetSitemapUrl(IConfiguration configuration)
        => configuration[SitemapUrlConfigKey] ?? DefaultSitemapUrl;

    /// <summary>
    /// Gets a source-specific cache key for the parsed API index.
    /// </summary>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The cache key used for the parsed API index.</returns>
    public static string GetIndexCacheKey(string sitemapUrl)
        => $"{IndexCacheKeyPrefix}{sitemapUrl.Trim()}";

    /// <summary>
    /// Replaces the scheme, host, and port of a canonical API page URL with the configured sitemap source.
    /// </summary>
    /// <param name="pageUrl">The canonical API page URL from the sitemap body.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The page URL rewritten to the configured host when both URLs are absolute; otherwise, the original page URL.</returns>
    public static string RebasePageUrl(string pageUrl, string sitemapUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ||
            !Uri.TryCreate(sitemapUrl, UriKind.Absolute, out var sitemapUri))
        {
            return pageUrl;
        }

        if (Uri.Compare(pageUri, sitemapUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) is 0)
        {
            return pageUrl;
        }

        var rebasedUri = new UriBuilder(pageUri)
        {
            Scheme = sitemapUri.Scheme,
            Host = sitemapUri.Host,
            Port = sitemapUri.IsDefaultPort ? -1 : sitemapUri.Port
        };

        return rebasedUri.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// Resolves a page URL to the markdown URL that should be fetched.
    /// </summary>
    /// <param name="pageUrl">The canonical page URL.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The markdown URL for the page.</returns>
    public static string BuildMarkdownUrl(string pageUrl, string sitemapUrl)
    {
        pageUrl = RebasePageUrl(pageUrl, sitemapUrl);

        if (pageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return pageUrl;
        }

        return $"{pageUrl.TrimEnd('/')}.md";
    }
}
