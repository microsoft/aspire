// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.Telemetry;
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

    [Theory]
    [InlineData(false, true, null, true)]
    [InlineData(false, true, null, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, null, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public async Task TemplateSourceList_ConsentControlsInitConfiguration(bool json, bool interactive, bool? useSource, bool accept)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var prompts = 0;
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (prompt, choices, _, _) =>
            {
                prompts++;
                Assert.Equal(ConfigSourceStrings.UseSourcePrompt, prompt);
                Assert.Equal([ConfigSourceStrings.Accept, ConfigSourceStrings.Decline], choices.Cast<string>());
                return accept ? ConfigSourceStrings.Accept : ConfigSourceStrings.Decline;
            }
        };
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (source, _) =>
            {
                Assert.Equal("https://git.example/configuration", source.Repository);
                Assert.Equal("config-preview", source.Ref);
                return Task.FromResult(("version-1", JsonNode.Parse("""
                    {
                      "features": { "must-not-be-materialized": true },
                      "templateSources": {
                        "enterprise": { "repository": "https://git.example/templates", "ref": "template-preview", "path": "templates" },
                        "personal": { "repository": "https://git.example/overridden", "ref": "must-not-leak" }
                      }
                    }
                    """)!.AsObject()));
            }
        };
        using var provider = CreateInheritedTemplateServices(workspace, interaction, git, interactive).BuildServiceProvider();
        var settings = provider.GetRequiredService<IConfigurationService>();
        var globalPath = settings.GetSettingsFilePath(true);
        const string globalSettings = """{"unrelated":true,"templateSources":{"Personal":{"path":"./personal"}}}""";
        await File.WriteAllTextAsync(globalPath, globalSettings);
        var localPath = settings.GetSettingsFilePath(false);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        const string localSettings = """{"templateSources":{"project-only":{"repository":"https://git.example/project"}}}""";
        await File.WriteAllTextAsync(localPath, localSettings);
        var options = json ? " --format json" : "";
        if (useSource is not null)
        {
            options += $" --use-config-source {useSource.Value.ToString().ToLowerInvariant()}";
        }

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"template source list{options}").InvokeAsync().DefaultTimeout();

        var shouldPrompt = !json && interactive && useSource is null;
        var consentRequired = !json && !interactive && useSource is null;
        var includeInherited = useSource is true || (shouldPrompt && accept);
        Assert.Equal(shouldPrompt ? 1 : 0, prompts);
        Assert.Equal(includeInherited ? 1 : 0, git.Calls);
        Assert.Equal(includeInherited ? [json ? GitCommandMode.Unattended : GitCommandMode.Interactive] : [], git.FetchModes);
        Assert.Equal(git.FetchModes, git.ResolutionModes);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Equal(globalSettings, await File.ReadAllTextAsync(globalPath));
        Assert.Equal(localSettings, await File.ReadAllTextAsync(localPath));
        Assert.Equal(consentRequired ? CliExitCodes.InvalidCommand : CliExitCodes.Success, result);
        if (consentRequired)
        {
            Assert.Equal(ConfigSourceStrings.ConsentRequired, Assert.Single(interaction.DisplayedErrors));
            Assert.Empty(interaction.DisplayedRawText);
            Assert.Empty(interaction.DisplayedRenderables);
            return;
        }

        Assert.Empty(interaction.DisplayedErrors);
        if (json)
        {
            var raw = Assert.Single(interaction.DisplayedRawText);
            Assert.Equal(ConsoleOutput.Standard, raw.ConsoleOverride);
            Assert.Equal(ConsoleOutput.Error, interaction.Console);
            Assert.Empty(interaction.DisplayedRenderables);
            await Verify(raw.Text, "json").UseParameters(json, interactive, useSource, accept);
        }
        else
        {
            Assert.Empty(interaction.DisplayedRawText);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
            console.Profile.Capabilities.Ansi = false;
            console.Profile.Width = 160;
            console.Write(Assert.Single(interaction.DisplayedRenderables));
            await Verify(writer.ToString()).UseParameters(json, interactive, useSource, accept);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task TemplateSourceList_JsonWithoutOptInDoesNotResolveInvalidSource(bool? useSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("JSON must not prompt.")
        };
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => throw new InvalidOperationException("JSON without opt-in must not acquire configuration.")
        };
        var services = CreateInheritedTemplateServices(workspace, interaction, git, interactive: true);
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = "--invalid",
            [CliConfigNames.InitSourceRef] = "--invalid"
        }));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"template source list --format json{(useSource is false ? " --use-config-source false" : "")}")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Empty(git.FetchModes);
        Assert.Empty(git.ResolutionModes);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Equal(("{}", (ConsoleOutput?)ConsoleOutput.Standard), Assert.Single(interaction.DisplayedRawText));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TemplateSourceList_WithoutCandidateDoesNotPromptOrFetch(bool json, bool? useSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("No candidate must not prompt.")
        };
        var git = new TestConfigSourceRepository();
        var detector = new TestConfigSourceDetector();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitClient>(git);
        services.AddSingleton<IEnvironment>(new TestEnvironment());
        services.AddSingleton<IInternalMicrosoftDetector>(detector);
        using var provider = services.BuildServiceProvider();
        var options = json ? " --format json" : "";
        if (useSource is not null)
        {
            options += $" --use-config-source {useSource.Value.ToString().ToLowerInvariant()}";
        }

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"template source list{options}").InvokeAsync().DefaultTimeout();

        Assert.Empty(git.FetchModes);
        Assert.Empty(git.ResolutionModes);
        Assert.Equal(!json || useSource is true ? 1 : 0, detector.Calls);
        Assert.Equal(useSource is true ? CliExitCodes.InvalidCommand : CliExitCodes.Success, result);
        if (useSource is true)
        {
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.NoCandidate, CliConfigNames.InitSourceUrl),
                Assert.Single(interaction.DisplayedErrors));
            Assert.Empty(interaction.DisplayedRawText);
        }
        else if (json)
        {
            Assert.Empty(interaction.DisplayedErrors);
            Assert.Equal(("{}", (ConsoleOutput?)ConsoleOutput.Standard), Assert.Single(interaction.DisplayedRawText));
        }
        else
        {
            Assert.Empty(interaction.DisplayedErrors);
            Assert.Equal(NewCommandStrings.TemplateSourceListEmpty, Assert.Single(interaction.DisplayedMessages).Message);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TemplateSourceList_DetectedCandidateRequiresConsentLikeNew(bool json, bool? useSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var prompts = 0;
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) =>
            {
                prompts++;
                return ConfigSourceStrings.Accept;
            }
        };
        var git = new TestConfigSourceRepository
        {
            ReadConfigurationWhenUnattended = true,
            ReadConfiguration = (source, _) =>
            {
                Assert.Equal(ConfigSourceService.MicrosoftRepository, source.Repository);
                Assert.Equal(AspireConfigFile.FileName, source.Path);
                Assert.Null(source.Ref);
                return Task.FromResult(("version-1", new JsonObject()));
            }
        };
        var detector = new TestConfigSourceDetector { IsInternal = true };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitClient>(git);
        services.AddSingleton<IEnvironment>(new TestEnvironment());
        services.AddSingleton<IInternalMicrosoftDetector>(detector);
        using var provider = services.BuildServiceProvider();
        var options = json ? " --format json" : "";
        if (useSource is not null)
        {
            options += $" --use-config-source {useSource.Value.ToString().ToLowerInvariant()}";
        }

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"template source list{options}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(!json && useSource is null ? 1 : 0, prompts);
        Assert.Equal(!json || useSource is true ? 1 : 0, detector.Calls);
        Assert.Equal(useSource is true || (!json && useSource is null) ? 1 : 0, git.Calls);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        if (json)
        {
            Assert.Equal(("{}", (ConsoleOutput?)ConsoleOutput.Standard), Assert.Single(interaction.DisplayedRawText));
        }
        Assert.False(File.Exists(provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true)));
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("template source list")]
    public async Task TemplateSourceList_ConfigSourceOptionMatchesNewAndRequiresBooleanValue(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestConfigSourceRepository();
        using var provider = CreateInheritedTemplateServices(workspace, interaction, git, interactive: true).BuildServiceProvider();
        var root = provider.GetRequiredService<RootCommand>();
        var omitted = root.Parse(command);
        Assert.Empty(omitted.Errors);
        Assert.Null(omitted.GetValue(ConfigSourceService.s_useSourceOption));
        foreach (var value in new[] { true, false })
        {
            var explicitValue = root.Parse($"{command} --use-config-source {value.ToString().ToLowerInvariant()}");
            Assert.Empty(explicitValue.Errors);
            Assert.Equal(value, explicitValue.GetValue(ConfigSourceService.s_useSourceOption));
        }
        var bareFlag = root.Parse($"{command} --use-config-source");

        Assert.NotEmpty(bareFlag.Errors);
        Assert.NotEqual(CliExitCodes.Success, await bareFlag.InvokeAsync().DefaultTimeout());
        Assert.Empty(git.FetchModes);
        Assert.Empty(git.ResolutionModes);
        Assert.Empty(interaction.DisplayedRawText);
    }
}
