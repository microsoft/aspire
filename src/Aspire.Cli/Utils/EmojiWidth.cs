// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides utilities for measuring and formatting emoji display widths in terminal output.
/// Accounts for Spectre.Console stripping Variation Selector-16 (U+FE0F) from emoji resolution,
/// which causes text-presentation-default emoji to render as a single cell in terminals.
/// </summary>
internal static class EmojiWidth
{
    private static readonly ConcurrentDictionary<string, int> s_cellLengthCache = new();

    /// <summary>
    /// Returns the cached cell width for <paramref name="emojiName"/>, computing it on first access.
    /// </summary>
    internal static int GetCachedCellWidth(string emojiName, IAnsiConsole console)
    {
        return s_cellLengthCache.GetOrAdd(emojiName, static (name, c) => GetCellWidth(name, c), console);
    }

    /// <summary>
    /// Computes the terminal cell width of the emoji identified by <paramref name="emojiName"/>.
    /// Detects text-presentation-default emoji (those with <c>Emoji_Presentation=No</c> in
    /// Unicode) and returns 1 for them, since Spectre.Console strips the Variation Selector-16
    /// (U+FE0F) during resolution and terminals render these as a single cell without it.
    /// </summary>
    internal static int GetCellWidth(string emojiName, IAnsiConsole console)
    {
        var resolved = Emoji.Replace($":{emojiName}:");

        // Emoji with Emoji_Presentation=No require FE0F for wide (2-cell) rendering.
        // Spectre strips FE0F during resolution, so terminals render them as 1 cell,
        // but Spectre's Measure may still report 2. Detect these and return 1.
        var enumerator = resolved.EnumerateRunes();
        if (enumerator.MoveNext())
        {
            var rune = enumerator.Current;
            if (!enumerator.MoveNext() && !HasDefaultEmojiPresentation(rune.Value))
            {
                return 1;
            }
        }

        var renderable = new Markup($":{emojiName}:");
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        return ((IRenderable)renderable).Measure(options, int.MaxValue).Max;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the Unicode code point has the
    /// <c>Emoji_Presentation=Yes</c> property, meaning it defaults to wide emoji
    /// rendering without a Variation Selector-16 suffix.
    /// </summary>
    /// <remarks>
    /// Based on Unicode 16.0 emoji-data.txt. Code points not listed here that
    /// are still emoji have <c>Emoji_Presentation=No</c> and render as narrow
    /// text characters (1 cell) when FE0F is absent.
    /// </remarks>
    private static bool HasDefaultEmojiPresentation(int codePoint)
    {
        return codePoint switch
        {
            // BMP: Miscellaneous Technical
            0x231A or 0x231B => true,
            >= 0x23E9 and <= 0x23EC => true,
            0x23F0 or 0x23F3 => true,
            // BMP: Geometric Shapes
            0x25FD or 0x25FE => true,
            // BMP: Miscellaneous Symbols
            0x2614 or 0x2615 => true,
            >= 0x2648 and <= 0x2653 => true,
            0x267F or 0x2693 or 0x26A1 => true,
            0x26AA or 0x26AB => true,
            0x26BD or 0x26BE => true,
            0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA => true,
            0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD => true,
            // BMP: Dingbats
            0x2702 or 0x2705 => true,
            >= 0x2708 and <= 0x270D => true,
            0x270F or 0x2712 or 0x2714 or 0x2716 => true,
            0x271D or 0x2721 or 0x2728 => true,
            0x2733 or 0x2734 or 0x2744 or 0x2747 => true,
            0x274C or 0x274E => true,
            >= 0x2753 and <= 0x2755 => true,
            0x2757 => true,
            0x2763 or 0x2764 => true,
            >= 0x2795 and <= 0x2797 => true,
            0x27A1 or 0x27B0 or 0x27BF => true,
            // BMP: Supplemental Arrows / Misc Symbols
            0x2934 or 0x2935 => true,
            >= 0x2B05 and <= 0x2B07 => true,
            0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
            // BMP: CJK Symbols
            0x3030 or 0x303D or 0x3297 or 0x3299 => true,
            // SMP: Mahjong, Playing Cards, Enclosed Ideographic
            0x1F004 or 0x1F0CF or 0x1F18E => true,
            >= 0x1F191 and <= 0x1F19A => true,
            >= 0x1F1E6 and <= 0x1F1FF => true,
            0x1F201 or 0x1F21A or 0x1F22F => true,
            >= 0x1F232 and <= 0x1F236 => true,
            >= 0x1F238 and <= 0x1F23A => true,
            0x1F250 or 0x1F251 => true,
            // SMP: Miscellaneous Symbols and Pictographs
            >= 0x1F300 and <= 0x1F320 => true,
            >= 0x1F32D and <= 0x1F335 => true,
            >= 0x1F337 and <= 0x1F37C => true,
            >= 0x1F37E and <= 0x1F393 => true,
            >= 0x1F3A0 and <= 0x1F3CA => true,
            >= 0x1F3CF and <= 0x1F3D3 => true,
            >= 0x1F3E0 and <= 0x1F3F0 => true,
            0x1F3F4 => true,
            >= 0x1F3F8 and <= 0x1F43E => true,
            0x1F440 => true,
            >= 0x1F442 and <= 0x1F4FC => true,
            >= 0x1F4FF and <= 0x1F53D => true,
            >= 0x1F54B and <= 0x1F54E => true,
            >= 0x1F550 and <= 0x1F567 => true,
            0x1F57A => true,
            0x1F595 or 0x1F596 => true,
            0x1F5A4 => true,
            >= 0x1F5FB and <= 0x1F64F => true,
            // SMP: Transport and Map Symbols
            >= 0x1F680 and <= 0x1F6C5 => true,
            0x1F6CC => true,
            >= 0x1F6D0 and <= 0x1F6D2 => true,
            >= 0x1F6D5 and <= 0x1F6D7 => true,
            >= 0x1F6DC and <= 0x1F6DF => true,
            0x1F6EB or 0x1F6EC => true,
            >= 0x1F6F4 and <= 0x1F6FC => true,
            // SMP: Geometric Shapes Extended, Chess Symbols
            >= 0x1F7E0 and <= 0x1F7EB => true,
            0x1F7F0 => true,
            // SMP: Supplemental Symbols and Pictographs
            >= 0x1F90C and <= 0x1F93A => true,
            >= 0x1F93C and <= 0x1F945 => true,
            >= 0x1F947 and <= 0x1F9FF => true,
            // SMP: Symbols and Pictographs Extended-A
            >= 0x1FA00 and <= 0x1FA53 => true,
            >= 0x1FA60 and <= 0x1FA6D => true,
            >= 0x1FA70 and <= 0x1FA7C => true,
            >= 0x1FA80 and <= 0x1FA89 => true,
            >= 0x1FA8F and <= 0x1FAC6 => true,
            >= 0x1FACE and <= 0x1FADC => true,
            >= 0x1FADF and <= 0x1FAE9 => true,
            >= 0x1FAF0 and <= 0x1FAF8 => true,
            _ => false,
        };
    }
}
