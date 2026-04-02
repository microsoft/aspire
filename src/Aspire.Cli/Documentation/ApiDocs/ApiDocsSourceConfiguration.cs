// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Resolves configuration for Aspire API docs sources.
/// </summary>
internal static class ApiDocsSourceConfiguration
{
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
    /// Resolves a page URL to the markdown URL that should be fetched.
    /// </summary>
    /// <param name="pageUrl">The canonical page URL.</param>
    /// <returns>The markdown URL for the page.</returns>
    public static string BuildMarkdownUrl(string pageUrl)
    {
        if (pageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return pageUrl;
        }

        return $"{pageUrl.TrimEnd('/')}.md";
    }
}
