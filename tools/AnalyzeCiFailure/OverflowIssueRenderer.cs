// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;

namespace AnalyzeCiFailure;

/// <summary>Pages complete aggregate reports and replaces only their explicitly managed content.</summary>
internal static class OverflowIssueRenderer
{
    private const string Start = "<!-- ci-failure-overflow-content:start -->";
    private const string End = "<!-- ci-failure-overflow-content:end -->";

    internal static bool HasMarker(string? body, string marker)
        => body is not null && (body.StartsWith(marker + "\n", StringComparison.Ordinal) ||
            body.StartsWith(marker + "\r\n", StringComparison.Ordinal));

    internal static IReadOnlyList<string> Pages(IEnumerable<string> rows, string runUrl)
    {
        var pages = new List<string>();
        var header = $"[automated] CI failure report\n\n[Source CI run]({runUrl})\n\n";
        var page = new StringBuilder(header);
        foreach (var row in rows)
        {
            if (Encoding.UTF8.GetByteCount(row) > 40000)
            {
                throw new ValidationException("One aggregate report entry exceeds the publication budget.");
            }
            if (Encoding.UTF8.GetByteCount(page.ToString()) + Encoding.UTF8.GetByteCount(row) > 50000)
            {
                pages.Add(page.ToString());
                page.Clear().Append(header);
            }
            page.Append(row);
        }
        pages.Add(page.ToString());
        return pages;
    }

    internal static string Merge(string? existing, string marker, string content)
    {
        var section = Start + "\n" + content + "\n" + End;
        if (existing is null)
        {
            return marker + "\n" + section + "\n";
        }
        // A diagnostic can contain "`<!-- ... -->`" inline. Only complete marker
        // lines delimit publisher-owned sections; inline text remains evidence.
        var starts = Regex.Matches(existing, "^" + Regex.Escape(Start) + @"\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var ends = Regex.Matches(existing, "^" + Regex.Escape(End) + @"\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!HasMarker(existing, marker) || starts.Count != 1 || ends.Count != 1 ||
            ends[0].Index < starts[0].Index)
        {
            throw new ValidationException("Aggregate report has missing or ambiguous managed sections.");
        }
        var result = existing[..starts[0].Index] + section + existing[(ends[0].Index + End.Length)..];
        ValidationRules.RequireCommentBudget(Encoding.UTF8.GetByteCount(result));
        return result;
    }
}
