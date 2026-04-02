// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Formats compact markdown for terminal display.
/// </summary>
internal static partial class TerminalMarkdownFormatter
{
    public static string Format(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        content = HeadingRegex().Replace(content, "\n\n$0");
        content = CodeBlockStartRegex().Replace(content, "\n$0\n");
        content = CodeBlockEndRegex().Replace(content, "\n$0\n");
        content = ExcessiveNewlinesRegex().Replace(content, "\n\n");

        return content.Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\s)(#{2,6}\s)")]
    private static partial System.Text.RegularExpressions.Regex HeadingRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\n)```\w*")]
    private static partial System.Text.RegularExpressions.Regex CodeBlockStartRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"```(?!\w)(?!\n)")]
    private static partial System.Text.RegularExpressions.Regex CodeBlockEndRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\n{3,}")]
    private static partial System.Text.RegularExpressions.Regex ExcessiveNewlinesRegex();
}
