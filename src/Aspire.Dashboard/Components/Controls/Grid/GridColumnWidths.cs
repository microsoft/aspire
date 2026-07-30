// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Dashboard.Components.Controls.Grid;

/// <summary>
/// Translates a CSS <c>grid-template-columns</c> string (as callers previously passed to the the previous library
/// data grid, e.g. <c>"1fr 1fr 0.5fr"</c>) into per-column widths for a native table's
/// <c>&lt;col&gt;</c> elements. Fractional tokens are converted to percentages; other tokens
/// (e.g. <c>"200px"</c>, <c>"auto"</c>) are used verbatim.
/// </summary>
public static class GridColumnWidths
{
    /// <summary>
    /// Parses the template into <paramref name="columnCount"/> width strings. If the template can't be
    /// parsed into that many columns, <c>"auto"</c> is used for the remainder.
    /// </summary>
    public static IReadOnlyList<string> Parse(string gridTemplateColumns, int columnCount)
    {
        var tokens = (gridTemplateColumns ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // When every token is fractional we can normalize them into percentages that sum to 100%.
        var fractions = new double[tokens.Length];
        var allFractional = tokens.Length > 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (TryParseFraction(tokens[i], out var value))
            {
                fractions[i] = value;
            }
            else
            {
                allFractional = false;
                break;
            }
        }

        var widths = new string[columnCount];
        if (allFractional && tokens.Length == columnCount)
        {
            var total = fractions.Sum();
            for (var i = 0; i < columnCount; i++)
            {
                widths[i] = total > 0
                    ? (fractions[i] / total * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%"
                    : "auto";
            }
        }
        else
        {
            for (var i = 0; i < columnCount; i++)
            {
                widths[i] = i < tokens.Length ? tokens[i] : "auto";
            }
        }

        return widths;
    }

    private static bool TryParseFraction(string token, out double value)
    {
        if (token.EndsWith("fr", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(token[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
