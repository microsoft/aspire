// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Helper methods for working with environment variables.
/// </summary>
internal static class EnvHelpers
{
    /// <summary>
    /// Converts environment variables to .env file format.
    /// </summary>
    /// <param name="environmentVariables">The environment variables to convert as key-value pairs.</param>
    /// <returns>A string in .env file format.</returns>
    public static string ConvertToEnvFormat(IEnumerable<KeyValuePair<string, string?>> environmentVariables)
    {
        var builder = new StringBuilder();

        foreach (var envVar in environmentVariables.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Format: KEY=VALUE
            // Handle values that contain special characters by quoting them if needed
            var value = envVar.Value ?? string.Empty;

            // Quote values that contain spaces, quotes, or other special characters
            if (NeedsQuoting(value))
            {
                // Escape special characters
                value = value.Replace("\\", "\\\\")  // Backslashes first
                             .Replace("\"", "\\\"")  // Quotes
                             .Replace("\n", "\\n")   // Newlines
                             .Replace("\r", "\\r")   // Carriage returns
                             .Replace("\t", "\\t");  // Tabs
                builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{envVar.Key}=\"{value}\"");
            }
            else
            {
                builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{envVar.Key}={value}");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Determines if a value needs to be quoted in a .env file.
    /// </summary>
    private static bool NeedsQuoting(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Quote if contains special characters that have meaning in .env files or shells:
        // - Space: word separator
        // - Double/single quotes: string delimiters
        // - $: variable interpolation
        // - \: escape character
        // - Newline/carriage return/tab: control characters
        // - #: comment character (if unquoted, everything after # is a comment)
        // - `: command substitution in some shells
        // - =: prevents parsing ambiguity (e.g., KEY=key=value vs KEY="key=value")
        // - Leading/trailing whitespace: would be trimmed
        return value.Contains(' ') ||
               value.Contains('"') ||
               value.Contains('\'') ||
               value.Contains('$') ||
               value.Contains('\\') ||
               value.Contains('\n') ||
               value.Contains('\r') ||
               value.Contains('\t') ||
               value.Contains('#') ||
               value.Contains('`') ||
               value.Contains('=') ||
               char.IsWhiteSpace(value[0]) ||
               char.IsWhiteSpace(value[^1]);
    }
}
