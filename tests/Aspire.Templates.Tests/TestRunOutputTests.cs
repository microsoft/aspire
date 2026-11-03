// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Xunit.Sdk;

namespace Aspire.Templates.Tests;

public class TestRunOutputTests
{
    // The ANSI placement is from the successful native MSTest run in Linux CI job 104979963967.
    private const string ColoredOutput =
        "\n\u001b[32mTest run summary: Passed!\u001b[90m - \u001b[m/work/starter.Tests.dll (net9.0|x64)\n" +
        "\u001b[m  total: 1\n" +
        "  failed: 0\n" +
        "\u001b[32m  succeeded: 1\n" +
        "\u001b[m  skipped: 0\n" +
        "  duration: 20s 211ms\n";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AssertSinglePassedMtpTest_AcceptsColoredOutput(string newline)
    {
        TestRunOutput.AssertSinglePassedMtpTest(ColoredOutput.Replace("\n", newline));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AssertSinglePassedMtpTest_AcceptsPlainOutput(string newline)
    {
        var output = string.Join(newline,
            "Test run summary: Passed! - starter.Tests.dll (net10.0|x64)",
            "  total: 1",
            "  failed: 0",
            "  succeeded: 1",
            "  skipped: 0",
            "  duration: 126ms");

        TestRunOutput.AssertSinglePassedMtpTest(output);
    }

    [Theory]
    [InlineData("Passed!", "Failed!")]
    [InlineData("total: 1", "total: 0")]
    [InlineData("total: 1", "total: 2")]
    [InlineData("failed: 0", "failed: 1")]
    [InlineData("succeeded: 1", "succeeded: 0")]
    [InlineData("succeeded: 1", "succeeded: 2")]
    [InlineData("skipped: 0", "skipped: 1")]
    [InlineData("skipped: 0", "skipped: 01")]
    public void AssertSinglePassedMtpTest_RejectsUnexpectedResults(string original, string replacement)
    {
        var output = ColoredOutput.Replace(original, replacement);

        Assert.Throws<MatchesException>(() => TestRunOutput.AssertSinglePassedMtpTest(output));
    }
}
