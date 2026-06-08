// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.Agents;

public class HookCommandFormatterTests
{
    [Theory]
    [InlineData("/home/user/.aspire/hooks/track-telemetry.sh", "'/home/user/.aspire/hooks/track-telemetry.sh'")]
    [InlineData("/home/space dir/x.sh", "'/home/space dir/x.sh'")]
    // bash single quotes are literal, so an apostrophe closes, escapes, and reopens: ' -> '\''
    [InlineData("/home/o'brien/x.sh", "'/home/o'\\''brien/x.sh'")]
    public void QuoteForBash_QuotesAndEscapesApostrophes(string input, string expected)
    {
        Assert.Equal(expected, HookCommandFormatter.QuoteForBash(input));
    }

    [Theory]
    [InlineData(@"C:\Users\user\.aspire\hooks\track-telemetry.ps1", @"'C:\Users\user\.aspire\hooks\track-telemetry.ps1'")]
    [InlineData(@"C:\Users\space dir\x.ps1", @"'C:\Users\space dir\x.ps1'")]
    // PowerShell single quotes are literal, so an apostrophe is doubled: ' -> ''
    [InlineData(@"C:\Users\o'brien\x.ps1", @"'C:\Users\o''brien\x.ps1'")]
    public void QuoteForPowerShell_QuotesAndDoublesApostrophes(string input, string expected)
    {
        Assert.Equal(expected, HookCommandFormatter.QuoteForPowerShell(input));
    }

    [Fact]
    public void BuildBashCommand_PrefixesBash()
    {
        Assert.Equal("bash '/x/y.sh'", HookCommandFormatter.BuildBashCommand("/x/y.sh"));
    }

    [Fact]
    public void BuildPowerShellCommand_UsesNoProfileBypassFile()
    {
        Assert.Equal(
            @"powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\x\y.ps1'",
            HookCommandFormatter.BuildPowerShellCommand(@"C:\x\y.ps1"));
    }
}
