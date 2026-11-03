// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace AnalyzeCiFailure;

/// <summary>Formats Markdown code spans and indentation without applying redaction policy.</summary>
internal static partial class MarkdownFormatting
{

    internal static string CodeSpan(string value)
    {
        var longest = BacktickRunRegex().Matches(value).Select(m => m.Length).DefaultIfEmpty(defaultValue: 0).Max();
        var delimiter = new string('`', longest + 1);

        return $"{delimiter} {value} {delimiter}";
    }

    internal static string Indent(string text, int spaces = 4)
        => string.Join('\n', text.Split('\n').Select(line => new string(' ', spaces) + line));

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRunRegex();
}
