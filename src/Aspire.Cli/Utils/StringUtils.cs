// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Utils;

internal static partial class StringUtils
{
    public static string RemoveSpectreFormatting(this string input)
    {
        return RemoveSpectreFormattingRegex().Replace(input, string.Empty).Trim();
    }

    [GeneratedRegex(@"\[[^\]]+\]")]
    private static partial Regex RemoveSpectreFormattingRegex();

    /// <summary>
    /// Calculates a fuzzy match score between a search term and a target string.
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="target">The target string to match against.</param>
    /// <returns>
    /// A score between 0.0 and 1.0, where 1.0 is a perfect match and 0.0 is no match.
    /// Higher scores indicate better matches. The caller is responsible for filtering based on score thresholds.
    /// </returns>
    /// <remarks>
    /// The scoring prioritizes different match types:
    /// <list type="bullet">
    /// <item>1.0 - Exact match (case-insensitive)</item>
    /// <item>0.95 - Target starts with search term</item>
    /// <item>0.85 - Target contains search term</item>
    /// <item>0.0-0.75 - Fuzzy match based on Levenshtein distance</item>
    /// </list>
    /// </remarks>
    public static double CalculateFuzzyScore(string searchTerm, string target)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || string.IsNullOrWhiteSpace(target))
        {
            return 0.0;
        }

        var searchLower = searchTerm.ToLowerInvariant();
        var targetLower = target.ToLowerInvariant();

        // Exact match
        if (searchLower == targetLower)
        {
            return 1.0;
        }

        // Starts with (high priority)
        if (targetLower.StartsWith(searchLower))
        {
            return 0.95;
        }

        // Contains (medium priority)
        if (targetLower.Contains(searchLower))
        {
            return 0.85;
        }

        // Levenshtein distance for fuzzy matching (low priority)
        var distance = GetLevenshteinDistance(searchLower, targetLower);
        var maxLength = Math.Max(searchLower.Length, targetLower.Length);

        // Normalize score: closer to 0 distance = higher score
        return (1.0 - (double)distance / maxLength) * 0.75;
    }

    /// <summary>
    /// Calculates the Levenshtein distance between two strings.
    /// </summary>
    /// <param name="source">The source string.</param>
    /// <param name="target">The target string.</param>
    /// <returns>The minimum number of single-character edits required to change the source string into the target string.</returns>
    public static int GetLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.IsNullOrWhiteSpace(target) ? 0 : target.Length;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return source.Length;
        }

        var sourceLength = source.Length;
        var targetLength = target.Length;
        var distance = new int[sourceLength + 1, targetLength + 1];

        for (var i = 0; i <= sourceLength; i++)
        {
            distance[i, 0] = i;
        }

        for (var j = 0; j <= targetLength; j++)
        {
            distance[0, j] = j;
        }

        for (var i = 1; i <= sourceLength; i++)
        {
            for (var j = 1; j <= targetLength; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[sourceLength, targetLength];
    }
}
