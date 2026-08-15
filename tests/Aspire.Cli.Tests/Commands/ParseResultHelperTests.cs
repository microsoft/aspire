// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using ParseResult = System.CommandLine.ParseResult;

namespace Aspire.Cli.Tests.Commands;

// xUnit requires public test classes and methods, so the internal enum stays strongly typed
// on a separate data provider while the theory accepts each value through object.
internal static class ParseResultHelperTestData
{
    public static TheoryData<string[], UnmatchedTokenPlacement, bool, string[], int> ProjectionCases => new()
    {
        {
            ["run", "--apphost=selector", "--isolated=false", "--debug", "--", "--apphost", "app-value"],
            UnmatchedTokenPlacement.Preserve,
            false,
            ["--isolated", "false", "--debug", "--", "--apphost", "app-value"],
            3
        },
        {
            ["start", "--debug", "--detach", "--unknown", "value"],
            UnmatchedTokenPlacement.AfterSeparator,
            false,
            ["--debug", "--", "--detach", "--unknown", "value"],
            1
        },
        {
            ["start", "--log-level", "Debug", "--unknown", "value"],
            UnmatchedTokenPlacement.AfterSeparator,
            false,
            ["--log-level", "Debug", "--", "--unknown", "value"],
            2
        },
        {
            ["run", "--project=selector", "--debug"],
            UnmatchedTokenPlacement.Preserve,
            false,
            ["--debug"],
            1
        },
        {
            // Repeating a single-value option produces a parse error, but the parse tree
            // still owns both value tokens and the projector must exclude both occurrences.
            ["run", "--apphost=first", "--apphost", "second", "--debug"],
            UnmatchedTokenPlacement.Preserve,
            true,
            ["--debug"],
            1
        },
        {
            ["run", "--apphost", "same-value", "--", "same-value"],
            UnmatchedTokenPlacement.Preserve,
            false,
            ["--", "same-value"],
            0
        },
        {
            ["start", "--debug", "--before", "before-value", "--", "after-value", "--after"],
            UnmatchedTokenPlacement.AfterSeparator,
            false,
            ["--debug", "--", "--before", "before-value", "after-value", "--after"],
            1
        },
        {
            ["run", "--debug", "--"],
            UnmatchedTokenPlacement.Preserve,
            false,
            ["--debug", "--"],
            1
        },
        {
            ["start", "--debug", "--"],
            UnmatchedTokenPlacement.AfterSeparator,
            false,
            ["--debug"],
            1
        }
    };
}

public sealed class ParseResultHelperTests : IDisposable
{
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
    [MemberData(nameof(ParseResultHelperTestData.ProjectionCases), MemberType = typeof(ParseResultHelperTestData))]
    public void GetForwardedArguments_ProjectsTokens(
        string[] commandLine,
        object unmatchedTokenPlacement,
        bool expectParseErrors,
        string[] expectedTokens,
        int expectedOptionCount)
    {
        var parseResult = _command.Parse(commandLine);
        var placement = Assert.IsType<UnmatchedTokenPlacement>(unmatchedTokenPlacement);

        Assert.Equal(expectParseErrors, parseResult.Errors.Count > 0);
        var forwardedArguments = GetForwardedArguments(parseResult, placement);

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
    public void GetForwardedArguments_NormalizesLiteralDoubleDashFileSystemInfoValue()
    {
        var parseResult = _command.Parse(["run", "--capture-profile-output=--"]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = GetForwardedArguments(parseResult, UnmatchedTokenPlacement.Preserve);

        Assert.Equal([$"--capture-profile-output={new FileInfo("--").FullName}"], forwardedArguments.Tokens);
        Assert.Equal(1, forwardedArguments.OptionCount);
    }

    [Fact]
    public void GetForwardedArguments_PreservesLiteralDoubleDashStringValue()
    {
        var valueOption = new System.CommandLine.Option<string>("--value");
        var command = new System.CommandLine.Command("test");
        command.Options.Add(valueOption);
        var parseResult = command.Parse(["test", "--value=--"]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = ParseResultHelper.GetForwardedArguments(
            parseResult,
            UnmatchedTokenPlacement.Preserve);

        Assert.Equal(["--value=--"], forwardedArguments.Tokens);
        Assert.Equal(1, forwardedArguments.OptionCount);
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

        var childParseResult = _command.Parse(["run", .. forwardedArguments.Tokens.ToArray()]);

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
