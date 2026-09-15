// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using ParseResult = System.CommandLine.ParseResult;

namespace Aspire.Cli.Tests.Commands;

internal static class ParseResultHelperTestData
{
    public static TheoryData<string[], bool, string[], int> ProjectionCases => new()
    {
        {
            ["run", "--apphost=selector", "--isolated=false", "--debug", "--", "--apphost", "app-value"],
            false,
            ["--isolated", "false", "--debug", "--", "--apphost", "app-value"],
            3
        },
        {
            ["start", "--debug", "--detach", "--unknown", "value"],
            false,
            ["--debug", "--", "--detach", "--unknown", "value"],
            1
        },
        {
            ["run", "--debug", "--secret", "value"],
            false,
            ["--debug", "--", "--secret", "value"],
            1
        },
        {
            ["start", "--log-level", "Debug", "--unknown", "value"],
            false,
            ["--log-level", "Debug", "--", "--unknown", "value"],
            2
        },
        {
            ["run", "--project=selector", "--debug"],
            false,
            ["--debug"],
            1
        },
        {
            // Repeating a single-value option produces a parse error, but the parse tree
            // still owns both value tokens and the projector must exclude both occurrences.
            ["run", "--apphost=first", "--apphost", "second", "--debug"],
            true,
            ["--debug"],
            1
        },
        {
            ["run", "--apphost", "same-value", "--", "same-value"],
            false,
            ["--", "same-value"],
            0
        },
        {
            ["start", "--debug", "--before", "before-value", "--", "after-value", "--after"],
            false,
            ["--debug", "--", "--before", "before-value", "after-value", "--after"],
            1
        },
        {
            ["start", "--capture-profile-output=--", "--", "--custom-arg"],
            false,
            [$"--capture-profile-output={new FileInfo("--").FullName}", "--", "--custom-arg"],
            1
        },
        {
            ["start", "--debug", "--"],
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
        bool expectParseErrors,
        string[] expectedTokens,
        int expectedOptionCount)
    {
        var parseResult = _command.Parse(commandLine);

        Assert.Equal(expectParseErrors, parseResult.Errors.Count > 0);
        var forwardedArguments = GetForwardedArguments(parseResult);

        Assert.Equal(expectedTokens, forwardedArguments.Tokens);
        Assert.Equal(expectedOptionCount, forwardedArguments.OptionCount);
    }

    [Fact]
    public void GetForwardedArguments_NormalizesSingleFileSystemInfoValue()
    {
        var relativePath = Path.Combine("Profile Output", "profile.zip");
        var parseResult = _command.Parse(["run", "--capture-profile-output", relativePath]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = GetForwardedArguments(parseResult);

        Assert.Equal(["--capture-profile-output", new FileInfo(relativePath).FullName], forwardedArguments.Tokens);
        Assert.Equal(2, forwardedArguments.OptionCount);
    }

    [Fact]
    public void GetForwardedArguments_PreservesInvalidFileSystemInfoValue()
    {
        var parseResult = _command.Parse(["run", "--capture-profile-output", ""]);

        Assert.NotEmpty(parseResult.Errors);
        var forwardedArguments = GetForwardedArguments(parseResult);

        Assert.Equal(["--capture-profile-output", ""], forwardedArguments.Tokens);
        Assert.Equal(2, forwardedArguments.OptionCount);
    }

    [Fact]
    public void GetForwardedArguments_NormalizesLiteralDoubleDashFileSystemInfoValue()
    {
        var parseResult = _command.Parse(["run", "--capture-profile-output=--"]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = GetForwardedArguments(parseResult);

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
        var forwardedArguments = ParseResultHelper.GetForwardedArguments(parseResult);

        Assert.Equal(["--value=--"], forwardedArguments.Tokens);
        Assert.Equal(1, forwardedArguments.OptionCount);
    }

    [Fact]
    public void ForwardedArguments_RoundTripsOptionShapedMultiValue()
    {
        var valuesOption = new System.CommandLine.Option<string[]>("--values")
        {
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var flagOption = new System.CommandLine.Option<bool>("--flag");
        var command = new System.CommandLine.Command("test");
        command.Options.Add(valuesOption);
        command.Options.Add(flagOption);

        var parentParseResult = command.Parse(["test", "--values=--flag"]);

        Assert.Empty(parentParseResult.Errors);
        var parentValues = Assert.IsType<string[]>(parentParseResult.GetValue(valuesOption));
        Assert.Equal(["--flag"], parentValues);
        Assert.False(parentParseResult.GetValue(flagOption));

        var forwardedArguments = ParseResultHelper.GetForwardedArguments(parentParseResult);
        var childParseResult = command.Parse(["test", .. forwardedArguments.Tokens]);

        Assert.Empty(childParseResult.Errors);
        Assert.Equal(parentValues, Assert.IsType<string[]>(childParseResult.GetValue(valuesOption)));
        Assert.Equal(parentParseResult.GetValue(flagOption), childParseResult.GetValue(flagOption));
        Assert.Equal(["--values=--flag"], forwardedArguments.Tokens);
    }

    [Fact]
    public void GetForwardedArguments_ExcludesOptionAliases()
    {
        var parseResult = _command.Parse(["run", "-d", "--isolated"]);

        Assert.Empty(parseResult.Errors);
        var forwardedArguments = ParseResultHelper.GetForwardedArguments(
            parseResult,
            RootCommand.DebugOption);

        Assert.Equal(["--isolated"], forwardedArguments.Tokens);
        Assert.Equal(1, forwardedArguments.OptionCount);
    }

    [Fact]
    public void ForwardedArguments_RoundTripsChildRunSemantics()
    {
        var parseResult = _command.Parse(
            ["run", "--apphost=selector", "--isolated=false", "--debug", "--", "--apphost", "app-value"]);
        var forwardedArguments = GetForwardedArguments(parseResult);

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

    [Fact]
    public void ForwardedArguments_RoundTripsDelimiterFreeAppHostArgumentsAfterSeparator()
    {
        var parseResult = _command.Parse(["run", "--debug", "--secret", "value"]);
        var forwardedArguments = GetForwardedArguments(parseResult);

        forwardedArguments.InsertCliOption("--non-interactive", "--capture-profile");

        Assert.Equal(3, forwardedArguments.OptionCount);
        Assert.Equal(
            ["--debug", "--non-interactive", "--capture-profile", "--", "--secret", "value"],
            forwardedArguments.Tokens);

        var childParseResult = _command.Parse(["run", .. forwardedArguments.Tokens]);

        Assert.Empty(childParseResult.Errors);
        Assert.True(childParseResult.GetValue(RootCommand.DebugOption));
        Assert.True(childParseResult.GetValue(RootCommand.NonInteractiveOption));
        Assert.True(childParseResult.GetValue(RootCommand.CaptureProfileOption));
        Assert.Equal(["--secret", "value"], childParseResult.UnmatchedTokens);
    }

    [Fact]
    public void GetLoggableArguments_RedactsForwardedTokensWithoutSeparator()
    {
        var parseResult = _command.Parse(["run", "--ApiKey", "sk-live-secret"]);
        var loggable = ParseResultHelper.GetLoggableArguments(parseResult);

        Assert.Equal("run <redacted> <redacted>", loggable);
        Assert.DoesNotContain("sk-live-secret", loggable, StringComparison.Ordinal);
        Assert.Equal("aspire run <redacted> <redacted>", $"aspire {loggable}");
    }

    [Fact]
    public void GetLoggableArguments_PreservesRecognizedOptionsAndRedactsTheRest()
    {
        var parseResult = _command.Parse(["run", "--debug", "--secret", "value"]);
        var loggable = ParseResultHelper.GetLoggableArguments(parseResult);

        Assert.Equal("run --debug <redacted> <redacted>", loggable);
        Assert.DoesNotContain("--secret", loggable, StringComparison.Ordinal);
    }

    [Fact]
    public void GetLoggableArguments_PreservesSeparatorAndRedactsTrailingTokens()
    {
        var parseResult = _command.Parse(["run", "--", "--ApiKey", "secret"]);
        var loggable = ParseResultHelper.GetLoggableArguments(parseResult);

        Assert.Equal("run -- <redacted> <redacted>", loggable);
        Assert.DoesNotContain("secret", loggable, StringComparison.Ordinal);
    }

    [Fact]
    public void GetLoggableArguments_PreservesCommandArguments()
    {
        var parseResult = _command.Parse(["add", "Aspire.Hosting.Redis"]);

        Assert.Equal("add Aspire.Hosting.Redis", ParseResultHelper.GetLoggableArguments(parseResult));
    }

    [Fact]
    public void GetLoggableArguments_PreservesRecognizedOptionValues()
    {
        var parseResult = _command.Parse(["run", "--apphost", "path/to/App.csproj"]);

        Assert.Equal("run --apphost path/to/App.csproj", ParseResultHelper.GetLoggableArguments(parseResult));
    }

    [Fact]
    public void GetLoggableArguments_ReturnsEmptyStringForBareInvocation()
    {
        Assert.Equal(string.Empty, ParseResultHelper.GetLoggableArguments(_command.Parse([])));

        // System.CommandLine keeps the first argument when it already names the root command, so
        // the synthesized and the user-supplied forms must both render as an empty argument list.
        Assert.Equal(string.Empty, ParseResultHelper.GetLoggableArguments(_command.Parse([_command.Name])));
    }

    [Fact]
    public void GetLoggableArguments_RedactsRepeatedValueOnlyInForwardedPosition()
    {
        var parseResult = _command.Parse(["run", "--apphost", "same-value", "--", "same-value"]);
        var loggable = ParseResultHelper.GetLoggableArguments(parseResult);

        Assert.Equal("run --apphost same-value -- <redacted>", loggable);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _workspace.Dispose();
    }

    private static ForwardedArguments GetForwardedArguments(ParseResult parseResult)
        => ParseResultHelper.GetForwardedArguments(
            parseResult,
            AppHostLauncher.s_appHostOption.InnerOption,
            AppHostLauncher.s_appHostOption.LegacyOption);
}
