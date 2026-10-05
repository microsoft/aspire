// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides shared helpers for console output formatting.
/// </summary>
internal static class ConsoleHelpers
{
    /// <summary>
    /// Formats an emoji prefix with trailing space for aligned console output.
    /// </summary>
    public static string FormatEmojiPrefix(KnownEmoji emoji, IAnsiConsole console, bool replaceEmoji = false, bool suppressColor = false)
    {
        const int emojiTargetWidth = 3; // 2 for emoji and 1 trailing space

        var cellLength = EmojiWidth.GetCachedCellWidth(emoji.Name, console);
        var padding = Math.Max(1, emojiTargetWidth - cellLength);
        var spectreEmojiText = $":{emoji.Name}:";

        if (replaceEmoji)
        {
            return Emoji.Replace(spectreEmojiText) + new string(' ', padding);
        }

        // Wrap in a color tag so monochrome text-presentation glyphs get a visible tint.
        // Terminals that render full-color emoji glyphs ignore ANSI foreground color, so this is always safe.
        // There is an option to suppress it in scenarios where the emoji is added to text inside an existing color.
        if (!suppressColor && emoji.TextColor is { } color)
        {
            return $"[{color}]{spectreEmojiText}[/]" + new string(' ', padding);
        }

        return spectreEmojiText + new string(' ', padding);
    }

    /// <summary>
    /// Creates a two-column grid with an emoji icon in the first column and content in the second.
    /// The first column is fixed-width so wrapped text in the second column stays aligned
    /// without pushing under the icon.
    /// </summary>
    public static Grid CreateEmojiGrid(KnownEmoji emoji, IAnsiConsole console, IRenderable content)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.Columns[0].NoWrap = true;
        grid.Columns[0].Padding = new Padding(0);
        grid.Columns[1].Padding = new Padding(0);

        grid.AddRow(
            new Markup(FormatEmojiPrefix(emoji, console)),
            content);

        return grid;
    }
}

