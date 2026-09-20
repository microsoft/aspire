// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.ConsoleLogs;
using Xunit;

namespace Aspire.Templates.Tests;

internal static class TestRunOutput
{
    public static void AssertSinglePassedMtpTest(string output)
    {
        // Native MTP reports a multiline summary, with ANSI colors on Linux even when redirected:
        // "\x1b[m  total: 1\n  failed: 0\n\x1b[32m  succeeded: 1\n\x1b[m  skipped: 0".
        // Strip presentation codes, but still require exactly one successful test and no skips.
        var plainOutput = AnsiParser.StripControlSequences(output);
        Assert.Matches(@"Test run summary: Passed![^\r\n]*\r?\n\s+total: 1\r?\n\s+failed: 0\r?\n\s+succeeded: 1\r?\n\s+skipped: 0(?:\r?\n|$)", plainOutput);
    }
}
