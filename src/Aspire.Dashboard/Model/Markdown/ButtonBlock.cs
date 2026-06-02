// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Markdown;

/// <summary>
/// Configuration for rendering a button from markdown.
/// Parsed from link-style syntax: [Text](type=button action=value arguments=value icon=value)
/// </summary>
public sealed class ButtonConfig
{
    public string Text { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses inline link-style button content (space-delimited key=value pairs).
    /// The first '=' in each pair separates key from value, allowing values to contain '='.
    /// The "type" key is skipped since it's only used as a discriminator.
    /// </summary>
    public static ButtonConfig ParseInline(string content)
    {
        var config = new ButtonConfig();

        // Split on spaces to get key=value pairs.
        // Values cannot contain unencoded spaces (use + for spaces in query strings).
        var pairs = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex == -1)
            {
                continue;
            }

            var key = pair.Substring(0, eqIndex);
            var value = pair.Substring(eqIndex + 1);

            switch (key.ToLowerInvariant())
            {
                case "type":
                    // Skip — used only as a discriminator to identify button links.
                    break;
                case "icon":
                    config.Icon = value;
                    break;
                default:
                    // "action", "arguments", and any other keys go into Values.
                    config.Values[key] = value;
                    break;
            }
        }

        return config;
    }
}
