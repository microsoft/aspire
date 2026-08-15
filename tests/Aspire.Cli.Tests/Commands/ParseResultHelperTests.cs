// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using ParseResult = System.CommandLine.ParseResult;

namespace Aspire.Cli.Tests.Commands;

public sealed class ParseResultHelperTests : IDisposable
{
    public static TheoryData<string[], bool, bool, string[], int> ProjectionCases => new()
    {
        {
            ["run", "--apphost=selector", "--isolated=false", "--debug", "--", "--apphost", "app-value"],
            true,
            false,
            ["--isolated", "false", "--debug", "--", "--apphost", "app-value"],
            3
        },
        {
            ["start", "--debug", "--detach", "--unknown", "value"],
            false,
            false,
            ["--debug", "--", "--detach", "--unknown", "value"],
            1
        },
        {
            ["start", "--log-level", "Debug", "--unknown", "value"],
            false,
            false,
            ["--log-level", "Debug", "--", "--unknown", "value"],
            2
        },
        {
            ["run", "--project=selector", "--debug"],
            true,
            false,
            ["--debug"],
            1
        },
        {
            // Repeating a single-value option produces a parse error, but the parse tree
            // still owns both value tokens and the projector must exclude both occurrences.
            ["run", "--apphost=first", "--apphost", "second", "--debug"],
            true,
            true,
            ["--debug"],
            1
        },
        {
            ["run", "--apphost", "same-value", "--", "same-value"],
            true,
            false,
            ["--", "same-value"],
            0
        },
        {
            ["start", "--capture-profile-output=--", "--", "--custom-arg"],
            false,
            false,
            ["--capture-profile-output=--", "--", "--custom-arg"],
            1
        },
        {
            ["run", "--debug", "--"],
            true,
            false,
            ["--debug", "--"],
            1
        },
        {
            ["start", "--debug", "--"],
            false,
            false,
            ["--debug"],
            1
        }
    };

    private readonly TemporaryWorkspace _workspace;
    private readonly ServiceProvider _provider;
    private readonly RootCommand _command;

    public ParseResultHelperTests(ITestOutputHelper outputHelper)
    {
        _workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        _provider = CliTestHelper.CreateServiceCollection(_workspace, outputHelper).BuildServiceProvider();
        _command = _provider.GetRequiredService<RootCommand>();
    }

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void GetForwardedArguments_ProjectsTokens(
        string[] commandLine,
        bool preserveUnmatchedTokens,
        bool expectParseErrors,
        string[] expectedTokens,
        int expectedOptionCount)
    {
        var parseResult = _command.Parse(commandLine);
        var unmatchedTokenPlacement = preserveUnmatchedTokens
            ? UnmatchedTokenPlacement.Preserve
            : UnmatchedTokenPlacement.AfterSeparator;

        Assert.Equal(expectParseErrors, parseResult.Errors.Count > 0);
        var forwardedArguments = GetForwardedArguments(parseResult, unmatchedTokenPlacement);

        Assert.Equal(expectedTokens, forwardedArguments.Tokens);
        Assert.Equal(expectedOptionCount, forwardedArguments.OptionCount);
    }

    [Fact]
    public void GetForwardedArguments_NormalizesSingleFileSystemInfoValue()
    {
        var relativePath = Path.Combine("Profile Output", "profile.zip");
        var parseResult = _command.Parse(["run", "--capture-profile-output", relativePath]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = GetForwardedArguments(parseResult, UnmatchedTokenPlacement.Preserve);

        Assert.Equal(["--capture-profile-output", new FileInfo(relativePath).FullName], forwardedArguments.Tokens);
        Assert.Equal(2, forwardedArguments.OptionCount);
    }

    [Fact]
    public void GetForwardedArguments_ExcludesOptionAliases()
    {
        var parseResult = _command.Parse(["run", "-d", "--isolated"]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = ParseResultHelper.GetForwardedArguments(
            parseResult,
            UnmatchedTokenPlacement.Preserve,
            RootCommand.DebugOption);

        Assert.Equal(["--isolated"], forwardedArguments.Tokens);
        Assert.Equal(1, forwardedArguments.OptionCount);
    }

    [Fact]
    public void ForwardedArguments_RoundTripsChildRunSemantics()
    {
        var parseResult = _command.Parse(
            ["run", "--apphost=selector", "--isolated=false", "--debug", "--", "--apphost", "app-value"]);
        var forwardedArguments = GetForwardedArguments(parseResult, UnmatchedTokenPlacement.Preserve);

        forwardedArguments.InsertCliOption("--non-interactive", "--capture-profile");

        Assert.Equal(5, forwardedArguments.OptionCount);
        Assert.Equal(
            ["--isolated", "false", "--debug", "--non-interactive", "--capture-profile", "--", "--apphost", "app-value"],
            forwardedArguments.Tokens);

        var childParseResult = _command.Parse(["run", .. forwardedArguments.Tokens]);

        Assert.Empty(childParseResult.Errors);
        Assert.False(childParseResult.GetValue(AppHostLauncher.s_isolatedOption));
        Assert.True(childParseResult.GetValue(RootCommand.DebugOption));
        Assert.True(childParseResult.GetValue(RootCommand.NonInteractiveOption));
        Assert.True(childParseResult.GetValue(RootCommand.CaptureProfileOption));
        Assert.Null(childParseResult.GetValue(AppHostLauncher.s_appHostOption));
        Assert.Equal(["--apphost", "app-value"], childParseResult.UnmatchedTokens);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _workspace.Dispose();
    }

    private static ForwardedArguments GetForwardedArguments(
        ParseResult parseResult,
        UnmatchedTokenPlacement unmatchedTokenPlacement)
        => ParseResultHelper.GetForwardedArguments(
            parseResult,
            unmatchedTokenPlacement,
            AppHostLauncher.s_appHostOption.InnerOption,
            AppHostLauncher.s_appHostOption.LegacyOption);
}
