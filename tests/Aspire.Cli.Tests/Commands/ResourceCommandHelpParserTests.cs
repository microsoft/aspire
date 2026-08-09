// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Commands;
using CommandLineRootCommand = System.CommandLine.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandHelpParserTests
{
    [Fact]
    public void Parse_WithResourceAndCommand_ReturnsCommandSpecificHelpRequest()
    {
        var (command, resourceArgument, commandArgument, appHostOption) = CreateCommand();

        var request = ResourceCommandHelpParser.Parse(
            command.Parse("resource web wait-for-browser --help"),
            resourceArgument,
            commandArgument,
            appHostOption);

        Assert.NotNull(request);
        Assert.Equal("web", request.ResourceName);
        Assert.Equal("wait-for-browser", request.CommandName);
        Assert.Null(request.AppHostProjectFile);
    }

    [Fact]
    public void Parse_WithAppHostOption_ReturnsAppHostProjectFile()
    {
        var (command, resourceArgument, commandArgument, appHostOption) = CreateCommand();

        var request = ResourceCommandHelpParser.Parse(
            command.Parse("resource web wait-for-browser --apphost ./AppHost/AppHost.csproj --help"),
            resourceArgument,
            commandArgument,
            appHostOption);

        Assert.NotNull(request);
        Assert.Equal("web", request.ResourceName);
        Assert.Equal("wait-for-browser", request.CommandName);
        var appHostProjectFile = Assert.IsType<FileInfo>(request.AppHostProjectFile);
        Assert.EndsWith(Path.Combine("AppHost", "AppHost.csproj"), appHostProjectFile.FullName);
    }

    [Fact]
    public void Parse_WithLegacyProjectOption_ReturnsAppHostProjectFile()
    {
        var (command, resourceArgument, commandArgument, appHostOption) = CreateCommand();

        var request = ResourceCommandHelpParser.Parse(
            command.Parse("resource web wait-for-browser --project ./AppHost/AppHost.csproj --help"),
            resourceArgument,
            commandArgument,
            appHostOption);

        Assert.NotNull(request);
        Assert.Equal("web", request.ResourceName);
        Assert.Equal("wait-for-browser", request.CommandName);
        var appHostProjectFile = Assert.IsType<FileInfo>(request.AppHostProjectFile);
        Assert.EndsWith(Path.Combine("AppHost", "AppHost.csproj"), appHostProjectFile.FullName);
    }

    [Theory]
    [InlineData("resource --help")]
    [InlineData("resource web --help")]
    [InlineData("resource web --apphost ./AppHost/AppHost.csproj --help")]
    [InlineData("resource web --project ./AppHost/AppHost.csproj --help")]
    [InlineData("resource web --message --help")]
    [InlineData("resource web --message=hi --help")]
    [InlineData("resource web --help -- --message help")]
    [InlineData("resource web -- --message hi --help")]
    public void Parse_WithGenericResourceHelp_ReturnsNull(string commandLine)
    {
        var (command, resourceArgument, commandArgument, appHostOption) = CreateCommand();

        var request = ResourceCommandHelpParser.Parse(
            command.Parse(commandLine),
            resourceArgument,
            commandArgument,
            appHostOption);

        Assert.Null(request);
    }

    private static (CommandLineRootCommand Command, Argument<string> ResourceArgument, Argument<string> CommandArgument, OptionWithLegacy<FileInfo?> AppHostOption) CreateCommand()
    {
        var resourceArgument = new Argument<string>("resource");
        var commandArgument = new Argument<string>("command");
        var appHostOption = new OptionWithLegacy<FileInfo?>("--apphost", "--project", "AppHost path");
        var resourceCommand = new Command("resource")
        {
            Arguments = { resourceArgument, commandArgument }
        };
        resourceCommand.Options.Add(appHostOption);

        var rootCommand = new CommandLineRootCommand { resourceCommand };

        return (rootCommand, resourceArgument, commandArgument, appHostOption);
    }
}
