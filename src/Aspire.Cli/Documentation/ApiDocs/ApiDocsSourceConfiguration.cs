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
    private const string MemberIndexCacheKeyPrefix = "member-index:";

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
        => $"{IndexCacheKeyPrefix}{GetSitemapContentCacheKey(sitemapUrl)}";

    /// <summary>
    /// Gets a source-specific cache key for the parsed member index.
    /// </summary>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The cache key used for the parsed member index.</returns>
    public static string GetMemberIndexCacheKey(string sitemapUrl)
        => $"{MemberIndexCacheKeyPrefix}{GetSitemapContentCacheKey(sitemapUrl)}";

    /// <summary>
    /// Gets the legacy raw-URL cache key for the parsed API index.
    /// </summary>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The legacy cache key used by earlier builds.</returns>
    public static string GetLegacyIndexCacheKey(string sitemapUrl)
        => $"{IndexCacheKeyPrefix}{sitemapUrl.Trim()}";

    /// <summary>
    /// Gets the cache key used for fetched sitemap content.
    /// </summary>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The cache key used for sitemap content and ETag persistence.</returns>
    public static string GetSitemapContentCacheKey(string sitemapUrl)
        => DocumentationCacheKey.FromUrl(sitemapUrl, "sitemap");

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
        pageUrl = StripFragment(pageUrl);
        pageUrl = RebasePageUrl(pageUrl, sitemapUrl);

        if (pageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return pageUrl;
        }

        return $"{pageUrl.TrimEnd('/')}.md";
    }

    /// <summary>
    /// Gets the cache key used for a fetched API markdown page.
    /// </summary>
    /// <param name="pageUrl">The canonical API page URL.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The cache key used for page content and ETag persistence.</returns>
    public static string GetPageContentCacheKey(string pageUrl, string sitemapUrl)
        => DocumentationCacheKey.FromUrl(BuildMarkdownUrl(pageUrl, sitemapUrl), "page");

    /// <summary>
    /// Resolves a markdown link from an API markdown page to its canonical non-markdown page URL.
    /// </summary>
    /// <param name="href">The markdown link target.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The resolved canonical page URL, or <c>null</c> if the link cannot be resolved.</returns>
    public static string? ResolveLinkedPageUrl(string href, string sitemapUrl)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(sitemapUrl, UriKind.Absolute, out var sitemapUri))
        {
            return null;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var resolvedUri))
        {
            resolvedUri = new Uri(sitemapUri, href);
        }

        var pageUrl = StripFragment(resolvedUri.GetLeftPart(UriPartial.Path));
        if (pageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            pageUrl = pageUrl[..^3];
        }

        return RebasePageUrl(pageUrl, sitemapUrl);
    }

    private static string StripFragment(string pageUrl)
    {
        var fragmentSeparatorIndex = pageUrl.IndexOf('#');
        return fragmentSeparatorIndex >= 0
            ? pageUrl[..fragmentSeparatorIndex]
            : pageUrl;
    }
}
