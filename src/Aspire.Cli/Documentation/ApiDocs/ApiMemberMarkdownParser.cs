// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Parses member-level C# API entries from type markdown pages.
/// </summary>
internal static partial class ApiMemberMarkdownParser
{
    /// <summary>
    /// Parses member-level API items from a C# type markdown page.
    /// </summary>
    /// <param name="typeItem">The parent type page item.</param>
    /// <param name="markdown">The markdown content for the type page.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <param name="memberGroupsByPageUrl">The known C# member-group pages keyed by canonical page URL.</param>
    /// <returns>The parsed member items.</returns>
    public static IReadOnlyList<ApiReferenceItem> Parse(
        ApiReferenceItem typeItem,
        string markdown,
        string sitemapUrl,
        IReadOnlyDictionary<string, ApiReferenceItem> memberGroupsByPageUrl)
    {
        if (!string.Equals(typeItem.Language, ApiReferenceLanguages.CSharp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(typeItem.Kind, ApiReferenceKinds.Type, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var lines = markdown.Split('\n');
        var items = new List<ApiReferenceItem>();

        foreach (var line in lines)
        {
            if (!TryParseMemberOverviewLine(line, out var name, out var href, out var summary))
            {
                continue;
            }

            var pageUrl = ApiDocsSourceConfiguration.ResolveLinkedPageUrl(href, sitemapUrl);
            if (pageUrl is null || !memberGroupsByPageUrl.TryGetValue(pageUrl, out var memberGroupItem))
            {
                continue;
            }

            var anchor = ExtractFragment(href);
            if (string.IsNullOrWhiteSpace(anchor))
            {
                continue;
            }

            items.Add(new ApiReferenceItem
            {
                Id = $"{memberGroupItem.Id}#{anchor}",
                Name = name,
                Language = memberGroupItem.Language,
                Kind = ApiReferenceKinds.Member,
                PageUrl = $"{memberGroupItem.PageUrl}#{anchor}",
                ParentId = memberGroupItem.Id,
                MemberGroup = memberGroupItem.MemberGroup,
                Summary = summary
            });
        }

        return items;
    }

    private static string? ExtractFragment(string href)
    {
        var fragmentSeparatorIndex = href.IndexOf('#');
        return fragmentSeparatorIndex >= 0 && fragmentSeparatorIndex < href.Length - 1
            ? href[(fragmentSeparatorIndex + 1)..].Trim()
            : null;
    }

    private static bool TryParseMemberOverviewLine(string line, out string name, out string href, out string? summary)
    {
        name = string.Empty;
        href = string.Empty;
        summary = null;

        var match = MemberOverviewRegex().Match(line.Trim());
        if (!match.Success)
        {
            return false;
        }

        href = match.Groups["href"].Value.Trim();
        if (!href.Contains("/reference/api/csharp/", StringComparison.OrdinalIgnoreCase) ||
            !href.Contains(".md#", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        name = match.Groups["name"].Value.Trim();
        if (name.Length is 0)
        {
            return false;
        }

        var tail = match.Groups["tail"].Value.Trim();
        summary = ExtractSummaryFromTail(tail);
        return true;
    }

    private static string? ExtractSummaryFromTail(string tail)
    {
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        var separatorIndex = tail.IndexOf(" -- ", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            separatorIndex = tail.IndexOf(" — ", StringComparison.Ordinal);
        }

        return separatorIndex >= 0 && separatorIndex < tail.Length - 4
            ? tail[(separatorIndex + 4)..].Trim()
            : null;
    }

    [GeneratedRegex(@"^-\s+\[(?<name>[^\]]+)\]\((?<href>[^)]+)\)(?<tail>.*)$")]
    private static partial Regex MemberOverviewRegex();
}
