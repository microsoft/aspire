// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Interaction;
using Spectre.Console;

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
    /// Escapes a message as Spectre markup while hyperlinking each occurrence of the specified file path.
    /// </summary>
    public static string EscapeMarkupWithFileLink(string message, string filePath)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(filePath);

        if (filePath.Length == 0)
        {
            return message.EscapeMarkup();
        }

        var index = message.IndexOf(filePath, StringComparison.Ordinal);
        if (index < 0)
        {
            return message.EscapeMarkup();
        }

        var linkedPath = FormatPathAsFileLink(filePath);
        var builder = new StringBuilder(message.Length + linkedPath.Length);
        var nextIndex = 0;

        while (index >= 0)
        {
            builder.Append(message[nextIndex..index].EscapeMarkup());
            builder.Append(linkedPath);
            nextIndex = index + filePath.Length;
            index = message.IndexOf(filePath, nextIndex, StringComparison.Ordinal);
        }

        builder.Append(message[nextIndex..].EscapeMarkup());

        return builder.ToString();
    }

    /// <summary>
    /// Formats a file path as Spectre link markup using a file URI target.
    /// </summary>
    public static string FormatPathAsFileLink(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (filePath.Length == 0)
        {
            return string.Empty;
        }

        var fileUri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri
            .Replace("[", "%5B", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal);

        return $"[link={fileUri.EscapeMarkup()}]{filePath.EscapeMarkup()}[/]";
    }
}
