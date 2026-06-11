// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides helpers for producing environment variable friendly names.
/// </summary>
internal static partial class EnvironmentVariableNameEncoder
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidNameRegex();

    /// <summary>
    /// Returns an environment-variable-safe representation of the provided name.
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <returns>A string that is safe to use as part of an environment variable.</returns>
    public static string Encode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrEmpty(name) || ValidNameRegex().IsMatch(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 1);

        if (char.IsAsciiDigit(name[0]))
        {
            builder.Append('_');
        }

        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }
}
