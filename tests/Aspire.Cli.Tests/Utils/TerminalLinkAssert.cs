// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Tests.Utils;

internal static class TerminalLinkAssert
{
    public static void ContainsLink(string output, string link, string text)
    {
        // Strip CSI SGR sequences (color codes) so they don't interfere with matching.
        output = Regex.Replace(output, @"\x1b\[[0-9;]*m", string.Empty);

        // Spectre.Console wraps long display text across multiple lines by closing and
        // reopening the OSC 8 hyperlink on each line. Extract all text segments from
        // OSC 8 sequences targeting the expected link and concatenate them.
        var escapedLink = Regex.Escape(link);
        var segmentPattern = $@"\x1b]8;id=\d+;{escapedLink}\x1b\\(?<text>.*?)\x1b]8;;\x1b\\";
        var matches = Regex.Matches(output, segmentPattern, RegexOptions.Singleline);

        Assert.True(matches.Count > 0, $"No OSC 8 link found for URL: {link}");

        var concatenated = string.Concat(matches.Select(m => m.Groups["text"].Value));
        Assert.Equal(text, concatenated);
    }
}
