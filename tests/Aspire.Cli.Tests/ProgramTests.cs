// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests;

public class ProgramTests
{
    [Fact]
    public void ParseLogFileOption_ReturnsNull_WhenArgsAreNull()
    {
        var result = Program.ParseLogFileOption(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLogFileOption_ReturnsValue_WhenOptionAppearsBeforeDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--log-file", "cli.log", "--", "--log-file", "app.log"]);

        Assert.Equal("cli.log", result);
    }

    [Fact]
    public void ParseLogFileOption_IgnoresValue_WhenOptionAppearsAfterDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--", "--log-file", "app.log"]);

        Assert.Null(result);
    }
}
