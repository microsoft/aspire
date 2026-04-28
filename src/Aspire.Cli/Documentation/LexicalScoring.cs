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

        foreach (var token in queryTokens)
        {
            var index = textSpan.IndexOf(token, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            score += baseMatchScore;

            if (IsWordBoundaryMatch(textSpan, token, index, wordCharacterMode))
            {
                score += wordBoundaryBonus;
            }

            var count = CountOccurrences(textSpan, token);
            if (count > 1)
            {
                score += Math.Min(count - 1, maxOccurrenceBonus) * multipleOccurrenceBonus;
            }
        }

        return score;
    }

    /// <summary>
    /// Counts the number of occurrences of a token in a span.
    /// </summary>
    /// <param name="text">The text to search.</param>
    /// <param name="token">The token to count.</param>
    /// <returns>The number of occurrences.</returns>
    public static int CountOccurrences(ReadOnlySpan<char> text, string token)
    {
        var count = 0;
        var startIndex = 0;

        while (startIndex < text.Length)
        {
            var nextIndex = text[startIndex..].IndexOf(token, StringComparison.Ordinal);
            if (nextIndex < 0)
            {
                break;
            }

            count++;
            startIndex += nextIndex + token.Length;
        }

        return count;
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
