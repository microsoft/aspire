// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components;

/// <summary>
/// Generates inline styles for console log resource prefixes.
/// </summary>
internal static class ResourcePrefixStyle
{
    internal const string DarkTextColor = "#000";

    internal static string GetStyle(string resourcePrefix)
    {
        var colorIndex = ColorGenerator.Instance.GetColorIndex(resourcePrefix);
        var accentVariableName = ColorGenerator.s_variableNames[colorIndex];
        var textColor = GetTextColorValue(accentVariableName);

        return $"background: var({accentVariableName}); --resource-text-color: {textColor};";
    }

    internal static string GetTextColorValue(string accentVariableName)
    {
        // Some accent variables need different foreground colors in light and dark themes.
        // Resolve through a CSS variable so each theme can keep WCAG AA contrast.
        // See https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html
        return $"var({GetTextColorVariableName(accentVariableName)}, {DarkTextColor})";
    }

    internal static string GetTextColorVariableName(string accentVariableName) => accentVariableName + "-text";
}
