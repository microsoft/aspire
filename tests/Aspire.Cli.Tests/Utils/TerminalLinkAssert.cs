// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Tests.Utils;

internal static class TerminalLinkAssert
{
    public static void ContainsLink(string output, string link, string text)
    {
        output = Regex.Replace(output, @"\x1b\[[0-9;]*m", string.Empty);

        var pattern = $@"\x1b]8;id=\d+;{Regex.Escape(link)}\x1b\\{Regex.Escape(text)}\x1b]8;;\x1b\\";
        Assert.Matches(pattern, output);
    }
}
