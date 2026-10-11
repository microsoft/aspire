// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace AnalyzeCiFailure.Common;

/// <summary>
/// Checks that agent-written text can be published without hiding or spoofing content.
/// The persistence sanitizers already strip these characters; this is the independent check
/// that the sanitized output really is safe before anything is published.
/// </summary>
internal static class SafeText
{
    /// <summary>A string of at most <paramref name="maxLength"/> code points with no line breaks or invisible characters.</summary>
    public static bool IsSingleLine(string? text, int maxLength)
        => text is not null && IsSafe(text, maxLength, allowTabAndNewline: false);

    /// <summary>Like <see cref="IsSingleLine"/>, but tab and newline are allowed.</summary>
    public static bool IsMultiline(string? text, int maxLength)
        => text is not null && IsSafe(text, maxLength, allowTabAndNewline: true);

    public static bool HasVisibleCharacter(string text)
        => text.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune));

    private static bool IsSafe(string text, int maxLength, bool allowTabAndNewline)
    {
        // Lengths are counted in code points, not UTF-16 units, so emoji and other
        // supplementary-plane characters count once.
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            length++;
            if (allowTabAndNewline && rune.Value is '\t' or '\n')
            {
                continue;
            }

            if (IsUnsafe(rune))
            {
                return false;
            }
        }

        return length <= maxLength;
    }

    private static bool IsUnsafe(Rune rune)
    {
        // Rune.GetUnicodeCategory is used rather than a \p{Cf} regex because .NET regexes match
        // UTF-16 units and miss supplementary-plane format characters such as U+E0001 (tags).
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
            // Variation selectors are invisible but are not in the Format category.
            || rune.Value is >= 0xFE00 and <= 0xFE0F
            || rune.Value is >= 0xE0100 and <= 0xE01EF;
    }
}
