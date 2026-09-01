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

        var existingEntries = ReadEntries(existingContent).ToHashSet(StringComparer.Ordinal);
        var existingUnanchoredEntries = existingEntries
            .Where(entry => !IsAnchored(entry))
            .ToHashSet(StringComparer.Ordinal);
        var existingAnchoredEntries = existingEntries
            .Where(IsAnchored)
            .Select(RemoveRoot)
            .ToHashSet(StringComparer.Ordinal);

        var missingEntries = ReadEntries(scaffoldContent)
            .Where(entry => !existingEntries.Contains(entry)
                && !ContainsCoveringEntry(existingUnanchoredEntries, existingAnchoredEntries, entry))
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
        // Blank lines and trailing whitespace do not participate in duplicate detection,
        // while the original content remains unchanged in the merged result.
        using var reader = new StringReader(content);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.TrimEnd();
            }
        }
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

    private static string RemoveRoot(string entry)
        => entry.StartsWith('/') ? entry[1..] : entry;

    private static bool IsAnchored(string entry)
    {
        var pattern = RemoveRoot(entry).TrimEnd('/');
        return entry.StartsWith('/') || pattern.Contains('/');
    }
}
