// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Maps a resource name to one of the Deck waterfall palette CSS variables
/// (<c>--trace-color-1</c> .. <c>--trace-color-14</c>, defined per theme in deck-theme.css).
/// </summary>
/// <remarks>
/// The per-resource <em>index</em> is taken from the shared <see cref="ColorGenerator"/> so a
/// resource keeps the same stable, distinct slot everywhere in the dashboard; only the palette is
/// swapped to the Deck trace colors here. This mirrors the Deck UI's <c>buildResourceColorMap</c>
/// (src/Aspire.Deck/ui/src/lib/colors.ts), which cycles the same 14-color palette.
/// </remarks>
internal static class TraceColorPalette
{
    // Must match the number of --trace-color-N tokens defined in deck-theme.css.
    private const int TraceColorCount = 14;

    public static string GetColorVariable(string resourceName)
    {
        var index = ColorGenerator.Instance.GetColorIndex(resourceName);
        return $"var(--trace-color-{(index % TraceColorCount) + 1})";
    }
}
