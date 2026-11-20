// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Configuration;

internal static class ConfigurationKey
{
    public static string[] Split(string key)
    {
        var parts = new List<string>();
        var segment = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var character = key[i];
            if (character == '\\')
            {
                // For example, templateSources.company\.templates.ref addresses the
                // literal source name company.templates, not a nested templates object.
                if (++i == key.Length || key[i] is not ('.' or '\\'))
                {
                    throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                        ConfigCommandStrings.ConfigurationKeyInvalid, key), nameof(key));
                }
                segment.Append(key[i]);
            }
            else if (character is '.' or ':')
            {
                parts.Add(segment.ToString());
                segment.Clear();
            }
            else
            {
                segment.Append(character);
            }
        }
        parts.Add(segment.ToString());
        return parts.ToArray();
    }

    public static string Join(IEnumerable<string> parts) => string.Join('.', parts.Select(EscapeSegment));

    public static string EscapeSegment(string segment) =>
        segment.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(".", "\\.", StringComparison.Ordinal);
}
