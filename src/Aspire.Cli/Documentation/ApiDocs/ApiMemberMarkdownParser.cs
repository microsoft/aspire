// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Parses member-level API entries from markdown pages that link to grouped member pages.
/// </summary>
internal static partial class ApiMemberMarkdownParser
{
    /// <summary>
    /// Parses member-level API items from a markdown page that contains grouped member links.
    /// </summary>
    /// <param name="containerItem">The parent page item.</param>
    /// <param name="markdown">The markdown content for the parent page.</param>
    /// <param name="sitemapUrl">The configured sitemap URL.</param>
    /// <param name="memberGroupsByPageUrl">The known grouped member pages keyed by canonical page URL.</param>
    /// <returns>The parsed member items.</returns>
    public static IReadOnlyList<ApiReferenceItem> Parse(
        ApiReferenceItem containerItem,
        string markdown,
        string sitemapUrl,
        IReadOnlyDictionary<string, ApiReferenceItem> memberGroupsByPageUrl)
    {
        if (string.IsNullOrWhiteSpace(markdown) || memberGroupsByPageUrl.Count is 0)
        {
            return [];
        }

        var lines = markdown.Split('\n');
        var candidates = new List<ParsedMemberCandidate>();

        foreach (var line in lines)
        {
            if (!TryParseMemberOverviewLine(line, out var name, out var href, out var summary))
            {
                continue;
            }

            var pageUrl = ApiDocsSourceConfiguration.ResolveLinkedPageUrl(href, sitemapUrl);
            if (pageUrl is null ||
                !memberGroupsByPageUrl.TryGetValue(pageUrl, out var memberGroupItem) ||
                !string.Equals(memberGroupItem.ParentId, containerItem.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var anchor = ExtractFragment(href);
            if (string.IsNullOrWhiteSpace(anchor))
            {
                continue;
            }

            candidates.Add(new ParsedMemberCandidate(name, anchor, summary, memberGroupItem));
        }

        if (candidates.Count is 0)
        {
            return [];
        }

        var slugCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var key = BuildSlugKey(candidate.MemberGroupItem.Id, candidate.Name);
            slugCounts[key] = slugCounts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var items = new List<ApiReferenceItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var memberId = BuildMemberId(candidate, slugCounts);
            items.Add(new ApiReferenceItem
            {
                Id = memberId,
                Name = candidate.Name,
                Language = candidate.MemberGroupItem.Language,
                Kind = ApiReferenceKinds.Member,
                PageUrl = $"{candidate.MemberGroupItem.PageUrl}#{candidate.Anchor}",
                ParentId = candidate.MemberGroupItem.Id,
                MemberGroup = candidate.MemberGroupItem.MemberGroup,
                Summary = candidate.Summary
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
        if (href.Length is 0)
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

    private static string BuildMemberId(ParsedMemberCandidate candidate, IReadOnlyDictionary<string, int> slugCounts)
    {
        var baseSlug = CreateMemberSlug(candidate.Name);
        var key = BuildSlugKey(candidate.MemberGroupItem.Id, candidate.Name);
        if (!slugCounts.TryGetValue(key, out var count) || count <= 1)
        {
            return $"{candidate.MemberGroupItem.Id}#{baseSlug}";
        }

        return $"{candidate.MemberGroupItem.Id}#{baseSlug}-{CreateShortSuffix(candidate.Anchor)}";
    }

    private static string BuildSlugKey(string memberGroupId, string memberName)
        => $"{memberGroupId}#{CreateMemberSlug(memberName)}";

    private static string CreateMemberSlug(string memberName)
    {
        var signatureStart = memberName.IndexOf('(');
        var baseName = signatureStart >= 0
            ? memberName[..signatureStart]
            : memberName;

        var slug = Slugify(baseName);
        return slug.Length is 0 ? "member" : slug;
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = builder.Length > 0;
            }
        }

        return builder.ToString();
    }

    private static string CreateShortSuffix(string anchor)
    {
        var xxHash = new XxHash3();
        xxHash.Append(Encoding.UTF8.GetBytes(anchor));
        return Convert.ToHexString(xxHash.GetCurrentHash()).ToLowerInvariant()[..8];
    }

    private sealed record ParsedMemberCandidate(string Name, string Anchor, string? Summary, ApiReferenceItem MemberGroupItem);
}
