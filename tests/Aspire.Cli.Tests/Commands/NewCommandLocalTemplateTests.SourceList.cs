// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    private const string SourceListSettings = """
        {
          // Management listing preserves the configured fields, including unfinished sources.
          "unrelated": "not listed",
          "templateSources": {
            "z-git": { "repository": "https://example.test/templates", "path": "templates/api", "ref": "release/17.0" },
            "Local [red]": { "path": "/templates/with [brackets]" },
            "empty": {},
            "git-head": { "repository": "https://example.test/head" },
            "unfinished": { "ref": "main" }
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateSourceList_DisplaysGlobalConfigurationWithoutDiscovery(bool json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IConfigurationService>();
        await File.WriteAllTextAsync(settings.GetSettingsFilePath(true), SourceListSettings);
        var localPath = settings.GetSettingsFilePath(false);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        const string localSettings = """{"templateSources":{"project-only":{"repository":"https://example.test/project"}}}""";
        await File.WriteAllTextAsync(localPath, localSettings);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(json ? "template source list --format json" : "template source list").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Empty(GetGitResolutions(git));
        Assert.Empty(GetGitFetches(git));
        Assert.Empty(interaction.ShownStatuses);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(SourceListSettings, await File.ReadAllTextAsync(settings.GetSettingsFilePath(true)));
        Assert.Equal(localSettings, await File.ReadAllTextAsync(localPath));
        if (json)
        {
            var output = Assert.Single(interaction.DisplayedRawText);
            Assert.Equal(ConsoleOutput.Standard, output.ConsoleOverride);
            Assert.Equal(ConsoleOutput.Error, interaction.Console);
            Assert.Empty(interaction.DisplayedRenderables);
            await Verify(output.Text, "json").UseParameters(json);
        }
        else
        {
            Assert.Empty(interaction.DisplayedRawText);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
            console.Profile.Capabilities.Ansi = false;
            console.Profile.Width = 160;
            console.Write(Assert.Single(interaction.DisplayedRenderables));
            await Verify(writer.ToString()).UseParameters(json);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"templateSources":{}}""")]
    [InlineData("""{"unrelated":true}""")]
    public async Task TemplateSourceList_EmptyJsonIsAnObjectWithoutCreatingOrChangingConfiguration(string? original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        if (original is not null)
        {
            await File.WriteAllTextAsync(path, original);
        }

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("template source list --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(("{}", (ConsoleOutput?)ConsoleOutput.Standard), Assert.Single(interaction.DisplayedRawText));
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedRenderables);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(original, File.Exists(path) ? await File.ReadAllTextAsync(path) : null);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" --format table")]
    public async Task TemplateSourceList_EmptyHumanReadableOutputHasGuidance(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"template source list{options}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(NewCommandStrings.TemplateSourceListEmpty, Assert.Single(interaction.DisplayedMessages).Message);
        Assert.Empty(interaction.DisplayedRawText);
        Assert.Empty(interaction.DisplayedRenderables);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"templateSources":null}""")]
    [InlineData("""{"templateSources":42}""")]
    public async Task TemplateSourceList_JsonReadFailureDoesNotReturnEmptySuccess(string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, original);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("template source list --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Single(interaction.DisplayedErrors);
        Assert.Equal(ConsoleOutput.Error, interaction.Console);
        Assert.Empty(interaction.DisplayedRawText);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TemplateSourceList_UnreadableConfigurationFailsWithoutJson()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();
        Directory.CreateDirectory(provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true));

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("template source list --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(NewCommandStrings.TemplateSourceReadFailed, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(ConsoleOutput.Error, interaction.Console);
        Assert.Empty(interaction.DisplayedRawText);
    }

    [Fact]
    public async Task TemplateSourceList_InvalidFormatIsRejected()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("template source list --format yaml");

        Assert.NotEmpty(parseResult.Errors);
        Assert.NotEqual(CliExitCodes.Success, await parseResult.InvokeAsync().DefaultTimeout());
        Assert.Empty(interaction.DisplayedRawText);
    }
}
