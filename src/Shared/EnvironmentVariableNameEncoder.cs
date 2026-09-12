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

    /// <summary>
    /// Returns an environment-variable-safe connection-string name that remains a single configuration key.
    /// </summary>
    /// <param name="name">The logical connection-string name.</param>
    /// <returns>A portable connection-string name.</returns>
    public static string EncodeConnectionStringName(string name)
    {
        var encodedName = Encode(name);
        if (!encodedName.Contains("__", StringComparison.Ordinal))
        {
            return encodedName;
        }

        // The environment configuration provider maps every "__" sequence to the ":" path delimiter.
        // Collapse underscore runs so the suffix remains one key under the ConnectionStrings section.
        // See https://learn.microsoft.com/dotnet/core/extensions/configuration-providers#environment-variable-configuration-provider.
        var builder = new StringBuilder(encodedName.Length);
        var previousWasUnderscore = false;
        foreach (var character in encodedName)
        {
            if (character == '_' && previousWasUnderscore)
            {
                continue;
            }

            builder.Append(character);
            previousWasUnderscore = character == '_';
        }

        return builder.ToString();
    }
}
