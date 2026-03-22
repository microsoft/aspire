// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    public static string FormatEmojiPrefix(KnownEmoji emoji, IAnsiConsole console)
    {
        const int emojiTargetWidth = 3; // 2 for emoji and 1 trailing space

        var cellLength = EmojiWidth.GetCachedCellWidth(emoji.Name, console);
        var padding = Math.Max(1, emojiTargetWidth - cellLength);
        return $":{emoji.Name}:" + new string(' ', padding);
    }
}
