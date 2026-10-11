// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Escapes literal values embedded in MSBuild properties and conditions.
/// </summary>
internal static class MSBuildEscaping
{
    internal static string Escape(string value)
    {
        // MSBuild interprets these characters even when the process argument is quoted.
        // https://learn.microsoft.com/visualstudio/msbuild/msbuild-special-characters
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '%' or '*' or '?' or '@' or '$' or '(' or ')' or ';' or '\'')
            {
                builder.Append('%');
                builder.Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
