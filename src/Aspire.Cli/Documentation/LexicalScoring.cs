// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Documentation;

/// <summary>
/// Controls which characters are treated as part of a lexical word when scoring boundaries.
/// </summary>
internal enum LexicalWordCharacterMode
{
    /// <summary>
    /// Treats only letters and digits as word characters.
    /// </summary>
    Alphanumeric,

    /// <summary>
    /// Treats letters, digits, underscores, and hyphens as word characters.
    /// </summary>
    IdentifierWithHyphen
}

/// <summary>
/// Provides shared helpers for lexical tokenization and field scoring.
/// </summary>
internal static class LexicalScoring
{
    /// <summary>
    /// Tokenizes text with the specified split expression.
    /// </summary>
    /// <param name="text">The input text.</param>
    /// <param name="tokenSplitRegex">The regular expression used to split the text into tokens.</param>
    /// <param name="minTokenLength">The minimum token length to keep.</param>
    /// <returns>The normalized unique tokens.</returns>
    public static string[] Tokenize(string text, Regex tokenSplitRegex, int minTokenLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return
        [
            .. tokenSplitRegex.Split(text)
                .Where(token => token.Length >= minTokenLength)
                .Select(static token => token.ToLowerInvariant())
                .Distinct()
        ];
    }

    /// <summary>
    /// Scores how well a normalized field matches the specified query tokens.
    /// </summary>
    /// <param name="lowerText">The normalized field text.</param>
    /// <param name="queryTokens">The normalized query tokens.</param>
    /// <param name="maxOccurrenceBonus">The maximum number of repeated-match bonuses to apply.</param>
    /// <param name="baseMatchScore">The base score for a token match.</param>
    /// <param name="wordBoundaryBonus">The additional score for a word-boundary match.</param>
    /// <param name="multipleOccurrenceBonus">The score applied for each additional token occurrence.</param>
    /// <param name="wordCharacterMode">The word-boundary behavior to use.</param>
    /// <returns>The lexical relevance score for the field.</returns>
    public static float ScoreField(
        string lowerText,
        string[] queryTokens,
        int maxOccurrenceBonus = 3,
        float baseMatchScore = 1.0f,
        float wordBoundaryBonus = 0.5f,
        float multipleOccurrenceBonus = 0.25f,
        LexicalWordCharacterMode wordCharacterMode = LexicalWordCharacterMode.Alphanumeric)
    {
        if (string.IsNullOrEmpty(lowerText))
        {
            return 0;
        }

        var score = 0.0f;
        var textSpan = lowerText.AsSpan();

        // Cap on how many occurrences we ever count toward the multi-occurrence bonus.
        // The bonus saturates at maxOccurrenceBonus, so beyond that one extra occurrence
        // there is no scoring reason to keep walking. Content fields can run several KB
        // and ScoreField is invoked once per (doc × field × query token), so capping the
        // per-field scan compounds across the whole corpus.
        var occurrenceLimit = maxOccurrenceBonus + 1;

        foreach (var token in queryTokens)
        {
            // Single forward scan: walk match-by-match, remember the first match
            // for the word-boundary bonus, and stop as soon as we've counted enough
            // occurrences to saturate the multi-occurrence bonus. The previous version
            // did IndexOf to find first match and then a second full-scan count,
            // doubling the bytes scanned in fields that contained a match.
            var startIndex = 0;
            var firstMatchIndex = -1;
            var count = 0;

            while (startIndex < textSpan.Length && count < occurrenceLimit)
            {
                var nextIndex = textSpan[startIndex..].IndexOf(token, StringComparison.Ordinal);
                if (nextIndex < 0)
                {
                    break;
                }

                var absoluteIndex = startIndex + nextIndex;
                if (firstMatchIndex < 0)
                {
                    firstMatchIndex = absoluteIndex;
                }

                count++;
                startIndex = absoluteIndex + token.Length;
            }

            if (count == 0)
            {
                continue;
            }

            score += baseMatchScore;

            if (IsWordBoundaryMatch(textSpan, token, firstMatchIndex, wordCharacterMode))
            {
                score += wordBoundaryBonus;
            }

            if (count > 1)
            {
                score += Math.Min(count - 1, maxOccurrenceBonus) * multipleOccurrenceBonus;
            }
        }

        return score;
    }

    /// <summary>
    /// Determines whether a match occurs on word boundaries.
    /// </summary>
    /// <param name="text">The containing text.</param>
    /// <param name="token">The matched token.</param>
    /// <param name="index">The match start index.</param>
    /// <param name="wordCharacterMode">The word-boundary behavior to use.</param>
    /// <returns><c>true</c> if the match occurs on word boundaries; otherwise, <c>false</c>.</returns>
    public static bool IsWordBoundaryMatch(
        ReadOnlySpan<char> text,
        string token,
        int index,
        LexicalWordCharacterMode wordCharacterMode = LexicalWordCharacterMode.Alphanumeric)
    {
        var startsAtBoundary = index == 0 || !IsWordCharacter(text[index - 1], wordCharacterMode);
        var endIndex = index + token.Length;
        var endsAtBoundary = endIndex >= text.Length || !IsWordCharacter(text[endIndex], wordCharacterMode);

        return startsAtBoundary && endsAtBoundary;
    }

    private static bool IsWordCharacter(char value, LexicalWordCharacterMode wordCharacterMode) => wordCharacterMode switch
    {
        LexicalWordCharacterMode.IdentifierWithHyphen => char.IsLetterOrDigit(value) || value is '_' or '-',
        _ => char.IsLetterOrDigit(value)
    };
}
