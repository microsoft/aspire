// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Merges scaffold-generated <c>.gitignore</c> entries with existing content.
/// </summary>
internal static class GitIgnoreMerger
{
    /// <summary>
    /// Appends missing scaffold entries while preserving existing content and line endings.
    /// </summary>
    internal static string Merge(string existingContent, string scaffoldContent)
    {
        ArgumentNullException.ThrowIfNull(existingContent);
        ArgumentNullException.ThrowIfNull(scaffoldContent);

        if (string.IsNullOrEmpty(existingContent))
        {
            return scaffoldContent;
        }

        var existingEntries = ReadEntries(existingContent).ToArray();
        var existingEntrySet = existingEntries.ToHashSet(StringComparer.Ordinal);
        var existingPositiveEntries = existingEntries
            .Where(entry => !IsNegated(entry))
            .ToHashSet(StringComparer.Ordinal);
        var existingUnanchoredEntries = existingPositiveEntries
            .Where(entry => !IsAnchored(entry))
            .ToHashSet(StringComparer.Ordinal);
        var existingAnchoredEntries = existingPositiveEntries
            .Where(IsAnchored)
            .Select(RemoveRoot)
            .ToHashSet(StringComparer.Ordinal);
        var existingNegatedPatterns = existingEntries
            .Where(IsNegated)
            .Select(RemoveNegation)
            .ToArray();
        var existingNegatedEntries = existingNegatedPatterns
            .Select(RemoveRoot)
            .ToHashSet(StringComparer.Ordinal);

        var missingEntries = ReadEntries(scaffoldContent)
            .Where(entry => !existingEntrySet.Contains(entry)
                && !ContainsCoveringEntry(existingUnanchoredEntries, existingAnchoredEntries, entry)
                && !ContainsCoveringEntry(
                    existingNegatedEntries,
                    RemoveRoot(IsNegated(entry) ? RemoveNegation(entry) : entry))
                && !ContainsConflictingNegatedDescendant(existingNegatedPatterns, entry))
            .ToArray();

        if (missingEntries.Length == 0)
        {
            return existingContent;
        }

        var newline = existingContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var mergedContent = existingContent;
        if (!mergedContent.EndsWith('\n'))
        {
            mergedContent += newline;
        }

        return mergedContent + string.Join(newline, missingEntries) + newline;
    }

    private static IEnumerable<string> ReadEntries(string content)
    {
        // Entries are line-oriented, for example:
        //   node_modules/
        //   /.aspire/
        //   !.aspire/
        // Blank lines and unescaped trailing spaces do not participate in duplicate detection.
        // An escaped trailing space, as in "keep\ ", remains part of the pattern. The original
        // content remains unchanged in the merged result.
        using var reader = new StringReader(content);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return TrimUnescapedTrailingSpaces(line);
            }
        }
    }

    private static string TrimUnescapedTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            var precedingBackslashes = 0;
            for (var index = end - 2; index >= 0 && line[index] == '\\'; index--)
            {
                precedingBackslashes++;
            }

            if (precedingBackslashes % 2 != 0)
            {
                break;
            }

            end--;
        }

        return line[..end];
    }

    private static bool ContainsCoveringEntry(
        HashSet<string> existingUnanchoredEntries,
        HashSet<string> existingAnchoredEntries,
        string scaffoldEntry)
    {
        var scaffoldEntryIsAnchored = IsAnchored(scaffoldEntry);
        var normalizedScaffoldEntry = RemoveRoot(scaffoldEntry);

        // An unanchored pattern matches at every level, so it also covers an anchored scaffold entry.
        // The inverse is not true: an anchored existing pattern must not suppress a broader unanchored entry.
        if (ContainsCoveringEntry(existingUnanchoredEntries, normalizedScaffoldEntry))
        {
            return true;
        }

        return scaffoldEntryIsAnchored &&
            ContainsCoveringEntry(existingAnchoredEntries, normalizedScaffoldEntry);
    }

    private static bool ContainsCoveringEntry(HashSet<string> existingEntries, string scaffoldEntry)
    {
        if (existingEntries.Contains(scaffoldEntry))
        {
            return true;
        }

        // A slashless pattern matches both a file and a directory, so it already covers a
        // generated directory-only pattern. The inverse is not true: "foo/" does not cover
        // a generated "foo" entry because it would leave a file named "foo" unignored.
        return scaffoldEntry.EndsWith('/') &&
            existingEntries.Contains(scaffoldEntry.TrimEnd('/'));
    }

    private static bool ContainsConflictingNegatedDescendant(
        IEnumerable<string> existingNegatedPatterns,
        string scaffoldEntry)
    {
        if (IsNegated(scaffoldEntry))
        {
            return false;
        }

        if (!scaffoldEntry.EndsWith('/'))
        {
            return false;
        }

        // Git cannot re-include a file when a later rule excludes its parent directory.
        // Preserve any existing negation whose match would be hidden by the scaffold entry.
        // See https://git-scm.com/docs/gitignore.
        return existingNegatedPatterns.Any(
            pattern => GitIgnorePatternMatcher.CanMatchDirectoryOrDescendant(pattern, scaffoldEntry));
    }

    private static string RemoveRoot(string entry)
        => entry.StartsWith('/') ? entry[1..] : entry;

    private static string RemoveNegation(string entry)
        => entry[1..];

    private static bool IsNegated(string entry)
        => entry.StartsWith('!');

    private static bool IsAnchored(string entry)
    {
        var pattern = RemoveRoot(entry).TrimEnd('/');
        return entry.StartsWith('/') || pattern.Contains('/');
    }

}
