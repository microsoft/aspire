// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides utilities for measuring emoji display widths in terminal output.
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
    /// Computes the terminal cell width of the emoji identified by <paramref name="emojiName"/>
    /// using Spectre.Console's measurement infrastructure.
    /// </summary>
    internal static int GetCellWidth(string emojiName, IAnsiConsole console)
    {
        var renderable = new Markup($":{emojiName}:");
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        return ((IRenderable)renderable).Measure(options, int.MaxValue).Max;
    }
}
