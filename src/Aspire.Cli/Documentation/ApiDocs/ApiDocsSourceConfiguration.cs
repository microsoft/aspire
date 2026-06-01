// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Resolves configuration for Aspire API docs sources.
/// </summary>
internal static partial class ApiDocsSourceConfiguration
{
    private const string IndexCacheKeyPrefix = "index:";
    private const string MemberIndexCacheKeyPrefix = "member-index:";
    
    /// <summary>
    /// Configuration path for overriding the API sitemap URL.
    /// </summary>
    public const string SitemapUrlConfigPath = "docs:api:sitemapUrl";

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
        => configuration[SitemapUrlConfigPath] ?? DefaultSitemapUrl;

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
    /// Rewrites site-relative markdown links to absolute URLs on the configured host so returned content is clickable.
    /// </summary>
    /// <param name="markdown">The markdown content to normalize.</param>
    /// <param name="pageUrl">The canonical page URL that produced the markdown.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <returns>The markdown with rewritten link targets.</returns>
    public static string RewriteMarkdownLinks(string markdown, string pageUrl, string sitemapUrl)
    {
        if (string.IsNullOrWhiteSpace(markdown) ||
            (!markdown.Contains("](/", StringComparison.Ordinal) && !markdown.Contains("](#", StringComparison.Ordinal)) ||
            !Uri.TryCreate(sitemapUrl, UriKind.Absolute, out var sitemapUri))
        {
            return markdown;
        }

        var siteRoot = sitemapUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var currentPageUrl = RebasePageUrl(StripFragment(pageUrl), sitemapUrl);

        return LocalMarkdownLinkRegex().Replace(markdown, match =>
        {
            var href = NormalizeMarkdownHref(match.Groups["href"].Value);
            var rewrittenHref = href[0] switch
            {
                '/' => $"{siteRoot}{href}",
                '#' => $"{currentPageUrl}{href}",
                _ => null
            };

            return rewrittenHref is null
                ? match.Value
                : $"[{match.Groups["text"].Value}]({rewrittenHref})";
        });
    }

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

        href = NormalizeMarkdownHref(href);

        Uri? resolvedUri;
        if (TryCreateHttpUri(href, out var httpUri))
        {
            resolvedUri = httpUri;
        }
        else
        {
            var siteRootUri = new Uri(sitemapUri.GetLeftPart(UriPartial.Authority));
            if (!Uri.TryCreate(siteRootUri, href, out var relativeUri) || relativeUri is null)
            {
                return null;
            }

            resolvedUri = relativeUri;
        }

        if (resolvedUri is null)
        {
            return null;
        }

        var pageUrl = StripFragment(resolvedUri.GetLeftPart(UriPartial.Path));
        if (pageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            pageUrl = pageUrl[..^3];
        }

        return RebasePageUrl(pageUrl, sitemapUrl);
    }

    private static bool TryCreateHttpUri(string href, out Uri? resolvedUri)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out resolvedUri) &&
            (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        resolvedUri = null;
        return false;
    }

    private static string NormalizeMarkdownHref(string href)
    {
        href = href.Trim();
        if (href.Length > 1 && href[0] is '<' && href[^1] is '>')
        {
            href = href[1..^1];
        }

        var titleSeparatorIndex = href.IndexOf(' ');
        return titleSeparatorIndex > 0
            ? href[..titleSeparatorIndex]
            : href;
    }

    private static string StripFragment(string pageUrl)
    {
        var fragmentSeparatorIndex = pageUrl.IndexOf('#');
        return fragmentSeparatorIndex >= 0
            ? pageUrl[..fragmentSeparatorIndex]
            : pageUrl;
    }

    [GeneratedRegex(@"\[(?<text>(?:[^\[\]]|\[[^\[\]]*\])+)\]\((?<href>(?:/|#)[^)]*)\)")]
    private static partial Regex LocalMarkdownLinkRegex();
}
