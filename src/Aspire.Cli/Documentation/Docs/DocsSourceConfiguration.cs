// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Documentation.Docs;

/// <summary>
/// Resolves configuration for the Aspire llms.txt docs source.
/// </summary>
internal static partial class DocsSourceConfiguration
{
    private const string IndexCacheKeyPrefix = "index:";
    
    /// <summary>
    /// Configuration path for overriding the llms.txt source URL.
    /// </summary>
    public const string LlmsTxtUrlConfigPath = "docs:llmsTxtUrl";

    /// <summary>
    /// Default URL for the abridged Aspire llms.txt documentation source.
    /// </summary>
    public const string DefaultLlmsTxtUrl = "https://aspire.dev/llms-small.txt";

    /// <summary>
    /// Gets the URL used to fetch the abridged Aspire llms.txt documentation source.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <returns>The resolved documentation source URL.</returns>
    public static string GetLlmsTxtUrl(IConfiguration configuration)
        => configuration[LlmsTxtUrlConfigPath] ?? DefaultLlmsTxtUrl;

    /// <summary>
    /// Gets a source-specific cache key for the parsed llms.txt index.
    /// </summary>
    /// <param name="llmsTxtUrl">The configured documentation source URL.</param>
    /// <returns>The cache key used for the parsed documentation index.</returns>
    public static string GetIndexCacheKey(string llmsTxtUrl)
        => $"{IndexCacheKeyPrefix}{GetContentCacheKey(llmsTxtUrl)}";

    /// <summary>
    /// Gets the legacy raw-URL cache key for the parsed llms.txt index.
    /// </summary>
    /// <param name="llmsTxtUrl">The configured documentation source URL.</param>
    /// <returns>The legacy cache key used by earlier builds.</returns>
    public static string GetLegacyIndexCacheKey(string llmsTxtUrl)
        => $"{IndexCacheKeyPrefix}{llmsTxtUrl.Trim()}";

    /// <summary>
    /// Gets the cache key used for the fetched llms.txt source content.
    /// </summary>
    /// <param name="llmsTxtUrl">The configured documentation source URL.</param>
    /// <returns>The cache key used for source content and ETag persistence.</returns>
    public static string GetContentCacheKey(string llmsTxtUrl)
        => DocumentationCacheKey.FromUrl(llmsTxtUrl, "llms");

    /// <summary>
    /// Rewrites docs markdown links so site-relative links are clickable on the configured host
    /// and in-page bookmarks are reduced to plain text.
    /// </summary>
    /// <param name="markdown">The markdown content to normalize.</param>
    /// <param name="llmsTxtUrl">The configured llms.txt URL.</param>
    /// <returns>The markdown with rewritten link targets.</returns>
    public static string RewriteMarkdownLinks(string markdown, string llmsTxtUrl)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var siteRoot = Uri.TryCreate(llmsTxtUrl, UriKind.Absolute, out var llmsUri)
            ? llmsUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : null;

        return MarkdownLinkRegex().Replace(markdown, match =>
        {
            var href = NormalizeMarkdownHref(match.Groups["href"].Value);
            var text = match.Groups["text"].Value;

            if (string.IsNullOrEmpty(href))
            {
                return match.Value;
            }

            return href[0] switch
            {
                '#' => text,
                '/' when siteRoot is not null => $"[{text}]({siteRoot}{href})",
                _ => match.Value
            };
        });
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

    [GeneratedRegex(@"(?<!!)\[(?<text>(?:[^\[\]]|\[[^\[\]]*\])+)\]\((?<href>[^)]*)\)")]
    private static partial Regex MarkdownLinkRegex();
}
