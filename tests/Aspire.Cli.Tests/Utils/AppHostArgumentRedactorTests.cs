// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class AppHostArgumentRedactorTests
{
    [Fact]
    public void Redact_NoSeparator_ReturnsArgumentsUnchanged()
    {
        string[] args = ["run", "--project", "AppHost.csproj"];

        Assert.Equal(args, AppHostArgumentRedactor.Redact(args));
    }

    [Fact]
    public void Redact_EmptyArguments_ReturnsEmpty()
    {
        Assert.Empty(AppHostArgumentRedactor.Redact([]));
    }

    [Fact]
    public void Redact_SeparatorWithTrailingTokens_RedactsEveryForwardedToken()
    {
        string[] args = ["run", "--project", "AppHost.csproj", "--", "--ConnectionStrings:db", "Server=db;Password=hunter2"];

        Assert.Equal(
            ["run", "--project", "AppHost.csproj", "--", "<redacted>", "<redacted>"],
            AppHostArgumentRedactor.Redact(args));
    }

    [Fact]
    public void Redact_SeparatorAsLastToken_KeepsSeparatorAndRedactsNothing()
    {
        string[] args = ["run", "--"];

        Assert.Equal(["run", "--"], AppHostArgumentRedactor.Redact(args));
    }

    [Fact]
    public void Redact_SeparatorAsOnlyToken_ReturnsSeparator()
    {
        Assert.Equal(["--"], AppHostArgumentRedactor.Redact(["--"]));
    }

    [Fact]
    public void Redact_LiteralSeparatorAfterFirstSeparator_IsRedacted()
    {
        // Only the first "--" is the CLI/AppHost boundary. A later "--" is AppHost input, so it is
        // redacted like every other forwarded token.
        string[] args = ["run", "--", "--secret", "--", "value"];

        Assert.Equal(
            ["run", "--", "<redacted>", "<redacted>", "<redacted>"],
            AppHostArgumentRedactor.Redact(args));
    }

    [Fact]
    public void Redact_SeparatorAsFirstToken_RedactsRemainder()
    {
        string[] args = ["--", "--ApiKey", "sk-live-secret"];

        Assert.Equal(["--", "<redacted>", "<redacted>"], AppHostArgumentRedactor.Redact(args));
    }

    [Fact]
    public void Redact_TokenContainingSeparatorAsSubstring_IsNotTreatedAsSeparator()
    {
        // "--foo" and "--=x" are not the bare separator, so nothing is redacted.
        string[] args = ["run", "--foo", "--=x"];

        Assert.Equal(["run", "--foo", "--=x"], AppHostArgumentRedactor.Redact(args));
    }

    [Fact]
    public void RedactToString_JoinsRedactedTokensWithSpaces()
    {
        string[] args = ["run", "--project", "AppHost.csproj", "--", "--ApiKey", "sk-live-secret"];

        Assert.Equal(
            "run --project AppHost.csproj -- <redacted> <redacted>",
            AppHostArgumentRedactor.RedactToString(args));
    }

    [Fact]
    public void RedactToString_NoSeparator_JoinsArgumentsVerbatim()
    {
        Assert.Equal("run --project AppHost.csproj", AppHostArgumentRedactor.RedactToString(["run", "--project", "AppHost.csproj"]));
    }
}
