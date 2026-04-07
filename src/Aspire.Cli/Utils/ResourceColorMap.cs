// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Assigns a consistent color to each resource name for colorized console output.
/// Colors are derived from the Aspire Dashboard's dark theme accent palette to provide
/// a consistent visual experience across the CLI and dashboard.
/// Colors are returned as hex strings (e.g. <c>#2CB7BD</c>) suitable for use in
/// Spectre.Console markup.
/// </summary>
internal sealed class ResourceColorMap
{
    /// <summary>
    /// Dark theme hex colors keyed by accent variable name from <see cref="ColorGenerator.s_variableNames"/>.
    /// The keys must match the variable names exactly so that the same palette index maps to the same
    /// visual color in both the CLI and the dashboard.
    /// </summary>
    internal static readonly Dictionary<string, string> s_hexColors = new()
    {
        ["--accent-teal"] = "#2CB7BD",
        ["--accent-marigold"] = "#F3D58E",
        ["--accent-brass"] = "#BF8B64",
        ["--accent-peach"] = "#FFC18F",
        ["--accent-coral"] = "#F89170",
        ["--accent-royal-blue"] = "#88A1F0",
        ["--accent-orchid"] = "#E19AD4",
        ["--accent-brand-blue"] = "#1A7ECF",
        ["--accent-seafoam"] = "#74D6C6",
        ["--accent-mink"] = "#B9B2A4",
        ["--accent-cyan"] = "#17A0A6",
        ["--accent-gold"] = "#E3BA7A",
        ["--accent-bronze"] = "#8E6038",
        ["--accent-orange"] = "#FFA44A",
        ["--accent-rust"] = "#EA6A3E",
        ["--accent-navy"] = "#2A4C8A",
        ["--accent-berry"] = "#D150C3",
        ["--accent-ocean"] = "#16728F",
        ["--accent-jade"] = "#51C0A5",
        ["--accent-olive"] = "#847B63",
    };

    private readonly ColorGenerator _palette = new();

    /// <summary>
    /// Gets the hex color string assigned to the specified resource name, assigning a new one if first seen.
    /// The returned value is a Spectre.Console markup-compatible hex color (e.g. <c>#2CB7BD</c>).
    /// </summary>
    public string GetColor(string resourceName)
    {
        var index = _palette.GetColorIndex(resourceName);
        var variableName = ColorGenerator.s_variableNames[index];
        return s_hexColors[variableName];
    }

    /// <summary>
    /// Pre-resolves colors for all provided resource names in sorted order so that
    /// color assignment is deterministic regardless of encounter order.
    /// </summary>
    public void ResolveAll(IEnumerable<string> resourceNames)
    {
        _palette.ResolveAll(resourceNames);
    }
}

