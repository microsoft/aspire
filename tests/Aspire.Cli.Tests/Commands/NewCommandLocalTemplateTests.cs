// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.Cli.Bundles;
using Aspire.Cli.Commands;
using Aspire.Cli.Git;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TemplateEngine.Abstractions;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ParameterWithoutDataTypeUsesTextPrompt(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.OmittedDataType", "name": "Omitted datatype", "shortName": "omitted-datatype",
              "symbols": {
                "Greeting": {
                  "type": "parameter", "displayName": "Greeting text",
                  "defaultValue": "Hello", "replaces": "GreetingValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "greeting.txt"), "GreetingValue");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Welcome");
        using var provider = CreateServices(workspace, interaction, interactive).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        if (interactive)
        {
            var prompt = Assert.Single(interaction.StringPromptCalls);
            Assert.Equal("Greeting text", prompt.PromptText);
            Assert.Equal("Hello", prompt.DefaultValue);
        }
        else
        {
            Assert.Empty(interaction.StringPromptCalls);
        }
        await Verify(ReadOutput(workspace)).UseParameters(interactive);
    }

    [Theory]
    [InlineData(true, "web|worker")]
    [InlineData(true, "web,worker")]
    [InlineData(true, "web,worker|api")]
    [InlineData(false, "web,worker")]
    [InlineData(false, "web|worker")]
    public async Task LocalTemplate_ChoiceDefaultsMatchEngineSemantics(bool allowMultipleValues, string defaultValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        string[] choices = allowMultipleValues ? ["unused", "web", "worker", "api"] : ["unused", defaultValue];
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), JsonSerializer.Serialize(new
        {
            identity = "Aspire.Local.ChoiceDefaults",
            name = "Choice defaults",
            shortName = "choice-defaults",
            symbols = new
            {
                Features = new
                {
                    type = "parameter",
                    datatype = "choice",
                    displayName = "Features",
                    allowMultipleValues,
                    defaultValue,
                    replaces = "FeaturesValue",
                    choices = choices.Select(choice => new { choice })
                }
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "features.txt"), "FeaturesValue");
        var interaction = new TestInteractionService();
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (prompt, options, _, _) =>
        {
            selectionCount++;
            Assert.Equal("Features", prompt);
            Assert.Equal([defaultValue, "unused"], options.Cast<string>());
            return options.Cast<string>().First();
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(allowMultipleValues ? 0 : 1, selectionCount);
        if (allowMultipleValues)
        {
            var prompt = Assert.Single(interaction.MultiSelectionPromptCalls);
            Assert.Equal("Features", prompt.PromptText);
            Assert.Equal(defaultValue.Contains("api", StringComparison.Ordinal)
                ? ["web", "worker", "api"] : ["web", "worker"], prompt.PreSelected.Cast<string>());
        }
        else
        {
            Assert.Empty(interaction.MultiSelectionPromptCalls);
        }
        await Verify(ReadOutput(workspace)).UseParameters(allowMultipleValues, defaultValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_PromptsFromEngineMetadataAndRenders(bool includeMessage)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Welcome");
        interaction.SetupBooleanResponse(includeMessage);
        interaction.PromptForSelectionCallback = (prompt, choices, _, _) =>
        {
            Assert.Equal("Service kind", prompt);
            Assert.Equal(["api", "worker"], choices.Cast<string>());
            return "worker";
        };
        interaction.PromptForSelectionsCallback = (prompt, choices, _, _) =>
        {
            Assert.Equal("Features", prompt);
            Assert.Equal(["web", "worker"], choices.Cast<string>());
            return ["web", "worker"];
        };

        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        var textPrompt = Assert.Single(interaction.StringPromptCalls);
        Assert.Equal("Greeting text", textPrompt.PromptText);
        Assert.Equal("Hello", textPrompt.DefaultValue);
        Assert.Equal("Include message", Assert.Single(interaction.BooleanPromptCalls).PromptText);
        await Verify(ReadOutput(workspace)).ScrubInlineGuids().UseParameters(includeMessage);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, "")]
    [InlineData(1, null)]
    [InlineData(2, null)]
    public async Task LocalTemplate_OptionalSingleChoiceCanBeSkippedOrSelected(int selectedIndex, string? defaultValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSingleChoiceTemplateAsync(workspace, required: false, defaultValue);
        var interaction = new TestInteractionService();
        var promptCount = 0;
        interaction.PromptForSelectionCallback = (prompt, choices, formatter, _) =>
        {
            promptCount++;
            Assert.Equal("Service kind", prompt);
            var options = choices.Cast<object>().ToArray();
            Assert.Equal(["(Skip)", "HTTP API", "Skip"], options.Select(formatter));
            return options[selectedIndex];
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(1, promptCount);
        if (selectedIndex == 0)
        {
            var nonInteractive = new TestInteractionService();
            using var nonInteractiveProvider = CreateServices(workspace, nonInteractive, interactive: false).BuildServiceProvider();
            var nonInteractiveExitCode = await nonInteractiveProvider.GetRequiredService<RootCommand>()
                .Parse("new --template-path template --name Demo --output non-interactive-out --suppress-agent-init")
                .InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, nonInteractiveExitCode);
            Assert.Empty(nonInteractive.DisplayedErrors);
            Assert.Equal(ReadOutput(Path.Combine(workspace.Path, "non-interactive-out")), ReadOutput(workspace));
        }
        await Verify(ReadOutput(workspace)).UseParameters(selectedIndex, defaultValue);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "api")]
    [InlineData(false, "Skip")]
    public async Task LocalTemplate_RequiredOrDefaultedSingleChoiceDoesNotOfferSkip(bool required, string? defaultValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSingleChoiceTemplateAsync(workspace, required, defaultValue);
        var interaction = new TestInteractionService();
        var promptCount = 0;
        interaction.PromptForSelectionCallback = (prompt, choices, formatter, _) =>
        {
            promptCount++;
            Assert.Equal("Service kind", prompt);
            var options = choices.Cast<object>().ToArray();
            Assert.Equal(defaultValue == "Skip" ? ["Skip", "HTTP API"] : ["HTTP API", "Skip"], options.Select(formatter));
            return options[0];
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(1, promptCount);
        await Verify(ReadOutput(workspace)).UseParameters(required, defaultValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ConditionalSingleChoiceOffersSkipOnlyWhenOptional(bool requireMode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.ConditionalSingleChoice",
              "name": "Conditional single choice",
              "shortName": "conditional-single-choice",
              "symbols": {
                "Mode": {
                  "type": "parameter", "datatype": "choice", "displayName": "Service kind",
                  "isRequired": "(RequireMode)", "replaces": "ModeValue",
                  "choices": [
                    { "choice": "api", "displayName": "HTTP API" },
                    { "choice": "Skip", "displayName": "Literal skip" }
                  ]
                },
                "RequireMode": {
                  "type": "parameter", "datatype": "bool", "defaultValue": "false"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.txt"), "Mode: [ModeValue]\n");
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(requireMode);
        var promptCount = 0;
        interaction.PromptForSelectionCallback = (prompt, choices, formatter, _) =>
        {
            promptCount++;
            Assert.Single(interaction.BooleanPromptCalls);
            Assert.Equal("Service kind", prompt);
            var options = choices.Cast<object>().ToArray();
            Assert.Equal(requireMode ? ["HTTP API", "Literal skip"] : ["Skip", "HTTP API", "Literal skip"], options.Select(formatter));
            return options[0];
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(1, promptCount);
        await Verify(ReadOutput(workspace)).UseParameters(requireMode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LocalTemplate_ConditionalEnablementUsesCollectedValues(bool useDatabase, bool dependentFirst)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateConditionalTemplateAsync(workspace, "isEnabled", "(UseDatabase)", !useDatabase, dependentFirst);
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(useDatabase);
        if (useDatabase)
        {
            interaction.SetupStringPromptResponse("Database=Demo");
        }
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal("Use database", Assert.Single(interaction.BooleanPromptCalls).PromptText);
        if (useDatabase)
        {
            var prompt = Assert.Single(interaction.StringPromptCalls);
            Assert.Equal("Connection string", prompt.PromptText);
            Assert.True(prompt.Required);
        }
        else
        {
            Assert.Empty(interaction.StringPromptCalls);
        }
        await Verify(ReadOutput(workspace)).UseParameters(useDatabase, dependentFirst);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LocalTemplate_ConditionalRequirednessUsesCollectedValues(bool useDatabase, bool dependentFirst)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateConditionalTemplateAsync(workspace, "isRequired", "(UseDatabase)", !useDatabase, dependentFirst);
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(useDatabase);
        interaction.SetupStringPromptResponse(useDatabase ? "Database=Demo" : string.Empty);
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal("Use database", Assert.Single(interaction.BooleanPromptCalls).PromptText);
        var prompt = Assert.Single(interaction.StringPromptCalls);
        Assert.Equal("Connection string", prompt.PromptText);
        Assert.Equal(useDatabase, prompt.Required);
        await Verify(ReadOutput(workspace)).UseParameters(useDatabase, dependentFirst);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ConditionalEnablementRemainsEngineValidatedWithoutInteraction(bool useDatabase)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateConditionalTemplateAsync(workspace, "isEnabled", "(UseDatabase)", useDatabase, dependentFirst: true);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(useDatabase ? CliExitCodes.FailedToCreateNewProject : CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Equal(!useDatabase, Directory.Exists(Path.Combine(workspace.Path, "out")));
        await Verify(new
        {
            interaction.DisplayedErrors,
            Files = useDatabase ? null : ReadOutput(workspace)
        }).UseParameters(useDatabase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ConditionalEnablementRemovesDisabledDefaults(bool useDatabase)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.ConditionalChain",
              "name": "Conditional chain",
              "shortName": "conditional-chain",
              "symbols": {
                "Token": {
                  "type": "parameter", "datatype": "string", "isRequired": true,
                  "isEnabled": "(Mode == \"cloud\")", "replaces": "TokenValue"
                },
                "Mode": {
                  "type": "parameter", "datatype": "string", "defaultValue": "cloud",
                  "isEnabled": "(UseDatabase)", "replaces": "ModeValue"
                },
                "UseDatabase": {
                  "type": "parameter", "datatype": "bool", "defaultValue": "true",
                  "replaces": "UseDatabaseValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.txt"),
            "Database enabled: UseDatabaseValue\nMode: ModeValue\nToken: TokenValue\n");
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(useDatabase);
        if (useDatabase)
        {
            interaction.SetupStringPromptResponse("cloud");
            interaction.SetupStringPromptResponse("template-token");
        }
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(useDatabase ? ["Mode", "Token"] : [], interaction.StringPromptCalls.Select(call => call.PromptText));
        await Verify(ReadOutput(workspace)).UseParameters(useDatabase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ConditionalRequirednessControlsMultiSelection(bool useDatabase)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.ConditionalMultiChoice",
              "name": "Conditional choices",
              "shortName": "conditional-choices",
              "symbols": {
                "Features": {
                  "type": "parameter", "datatype": "choice", "allowMultipleValues": true,
                  "isRequired": "(UseDatabase)", "replaces": "FeaturesValue",
                  "choices": [{ "choice": "web" }, { "choice": "worker" }]
                },
                "UseDatabase": {
                  "type": "parameter", "datatype": "bool", "defaultValue": "false",
                  "replaces": "UseDatabaseValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.txt"),
            "Database enabled: UseDatabaseValue\nFeatures: [FeaturesValue]\n");
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(useDatabase);
        interaction.PromptForSelectionsCallback = (_, _, _, _) => useDatabase ? ["web"] : [];
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        var prompt = Assert.Single(interaction.MultiSelectionPromptCalls);
        Assert.Equal("Features", prompt.PromptText);
        Assert.Equal(!useDatabase, prompt.Optional);
        await Verify(ReadOutput(workspace)).UseParameters(useDatabase);
    }

    [Theory]
    [InlineData("isEnabled")]
    [InlineData("isRequired")]
    public async Task LocalTemplate_ConditionalEvaluationErrorFailsBeforeWriting(string conditionProperty)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateConditionalTemplateAsync(workspace, conditionProperty, "\"not-a-boolean\"", defaultUseDatabase: true, dependentFirst: false);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        await Verify(interaction.DisplayedErrors).UseParameters(conditionProperty);
    }

    [Fact]
    public async Task LocalTemplate_ConditionalCycleStillUsesEngineValidation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.ConditionalCycle",
              "name": "Conditional cycle",
              "shortName": "conditional-cycle",
              "symbols": {
                "First": {
                  "type": "parameter", "datatype": "bool", "defaultValue": "true",
                  "isEnabled": "(Second)", "replaces": "FirstValue"
                },
                "Second": {
                  "type": "parameter", "datatype": "bool", "defaultValue": "true",
                  "isEnabled": "(First)", "replaces": "SecondValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.txt"), "First: FirstValue\nSecond: SecondValue\n");
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(true);
        interaction.SetupBooleanResponse(true);
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(["First", "Second"], interaction.BooleanPromptCalls.Select(call => call.PromptText));
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ExcludesGitMetadataWithoutChangingSource(bool gitFile)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var gitDirectory = workspace.CreateDirectory(gitFile ? "git-storage" : "template/.git");
        await File.WriteAllTextAsync(Path.Combine(gitDirectory.FullName, "config"), "Original repository configuration");
        if (gitFile)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".git"), "gitdir: ../git-storage");
        }

        var nestedGitDirectory = workspace.CreateDirectory("template/nested/.git");
        await File.WriteAllTextAsync(Path.Combine(nestedGitDirectory.FullName, "config"), "Nested repository configuration");
        var nestedWorktree = workspace.CreateDirectory("template/worktree");
        await File.WriteAllTextAsync(Path.Combine(nestedWorktree.FullName, ".git"), "gitdir: ../../git-storage");
        await File.WriteAllTextAsync(Path.Combine(nestedWorktree.FullName, "content.txt"), "Keep worktree content");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".gitignore"), "bin/");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".gitattributes"), "* text=auto");
        var github = workspace.CreateDirectory("template/.github");
        await File.WriteAllTextAsync(Path.Combine(github.FullName, "content.txt"), "Keep GitHub content");
        var originalSource = ReadOutput(Path.Combine(workspace.Path, "template"));
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(originalSource, ReadOutput(Path.Combine(workspace.Path, "template")));
        Assert.Equal("Original repository configuration", await File.ReadAllTextAsync(Path.Combine(gitDirectory.FullName, "config")));
        await Verify(ReadOutput(workspace)).ScrubInlineGuids().UseParameters(gitFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ExcludesGitMetadataFromDeepSourcePaths(bool gitFile)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var templateDirectory = Path.Combine(Enumerable.Repeat("d", 50).Append("template").ToArray());
        var templatePath = Path.Combine(workspace.Path, templateDirectory);
        Assert.True(templatePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Length > 47);
        var config = workspace.CreateDirectory(Path.Combine(templateDirectory, ".template.config"));
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            { "identity": "Aspire.Local.DeepPath", "name": "Deep path template", "shortName": "deep-path" }
            """);
        if (gitFile)
        {
            await File.WriteAllTextAsync(Path.Combine(templatePath, ".git"), "gitdir: ../git-storage");
        }
        else
        {
            var git = workspace.CreateDirectory(Path.Combine(templateDirectory, ".git"));
            await File.WriteAllTextAsync(Path.Combine(git.FullName, "config"), "Original repository configuration");
        }
        var nestedGit = workspace.CreateDirectory(Path.Combine(templateDirectory, "nested", ".git"));
        await File.WriteAllTextAsync(Path.Combine(nestedGit.FullName, "config"), "Nested repository configuration");
        await File.WriteAllTextAsync(Path.Combine(templatePath, "content.txt"), "Keep template content");
        await File.WriteAllTextAsync(Path.Combine(templatePath, ".gitignore"), "bin/");
        var github = workspace.CreateDirectory(Path.Combine(templateDirectory, ".github"));
        await File.WriteAllTextAsync(Path.Combine(github.FullName, "content.txt"), "Keep GitHub content");
        var originalSource = ReadOutput(templatePath);
        using var host = new TemplateEngineHost([]);
        var fileSystem = ((ITemplateEngineHost)host).FileSystem;
        // Engine source defaults can also hide .git; verify the host's absolute-path filtering directly.
        Assert.False(fileSystem.FileExists(Path.Combine(templatePath, ".git")));
        Assert.False(fileSystem.DirectoryExists(Path.Combine(templatePath, ".git")));
        Assert.False(fileSystem.FileExists(Path.Combine(nestedGit.FullName, "config")));
        Assert.Throws<FileNotFoundException>(() => fileSystem.ReadAllText(Path.Combine(nestedGit.FullName, "config")));
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["new", "--template-path", templatePath, "--name", "Demo", "--output", "out", "--suppress-agent-init"])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(originalSource, ReadOutput(templatePath));
        await Verify(ReadOutput(workspace)).UseParameters(gitFile);
    }

    [Fact]
    public async Task LocalTemplate_NonInteractiveUsesEngineDefaults()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IPackagingService>(new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Templates without AspireVersion or AspireNuGetSource must not resolve package channels.")
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace)).ScrubInlineGuids();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_WaitsForBundleExtractionOnlyAfterSuccessfulCreation(bool succeeds)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template", requiredGreeting: !succeeds);
        var interaction = new TestInteractionService();
        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExtraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bundleService = new TestBundleService(isBundle: true)
        {
            EnsureExtractedAsyncCallback = async cancellationToken =>
            {
                extractionStarted.SetResult();
                await allowExtraction.Task.WaitAsync(cancellationToken);
            }
        };
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IBundleService>(bundleService);
        using var provider = services.BuildServiceProvider();

        var invocationTask = provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync();

        bool outputCreated;
        bool waitsForExtraction;
        try
        {
            await Task.WhenAny(extractionStarted.Task, invocationTask).DefaultTimeout();
            outputCreated = File.Exists(Path.Combine(workspace.Path, "out", "Demo.cs"));
            waitsForExtraction = extractionStarted.Task.IsCompletedSuccessfully && !invocationTask.IsCompleted;
        }
        finally
        {
            allowExtraction.SetResult();
        }

        var exitCode = await invocationTask.DefaultTimeout();

        Assert.Equal(succeeds ? CliExitCodes.Success : CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Equal(succeeds, outputCreated);
        Assert.Equal(succeeds, extractionStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(succeeds, waitsForExtraction);
    }

    [Theory]
    [InlineData(true, "AspireVersion", false)]
    [InlineData(false, "AspireVersion", false)]
    [InlineData(true, "AspireVersion", true)]
    [InlineData(false, "AspireVersion", true)]
    [InlineData(true, "aspireVersion", true)]
    [InlineData(false, "aspireVersion", true)]
    public async Task LocalTemplate_InjectsCurrentAspireVersionWithoutPrompting(bool interactive, string parameterName, bool hasDefault)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        var defaultProperty = hasDefault ? "\"defaultValue\": \"1.0.0\"," : "";
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), $$"""
            {
              "identity": "Aspire.Local.Version",
              "name": "Versioned template",
              "shortName": "versioned",
              "symbols": {
                "{{parameterName}}": {
                  "type": "parameter",
                  "datatype": "string",
                  "isRequired": true,
                  {{defaultProperty}}
                  "replaces": "AspireVersionValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "aspire.config.json"), """
            { "packages": { "Aspire.Hosting.JavaScript": "AspireVersionValue" } }
            """);

        const string packageVersion = "17.0.0-pr.12345.gabcdef12";
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive);
        var context = workspace.CreateExecutionContext(
            identityChannel: "pr-12345",
            identityVersion: $"{packageVersion}+abcdef123456",
            identityOverridden: true);
        context.HivesDirectory.CreateSubdirectory("pr-12345/packages");
        services.AddSingleton(context);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        using var generatedConfig = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "aspire.config.json")));
        Assert.Equal(packageVersion, generatedConfig.RootElement.GetProperty("packages").GetProperty("Aspire.Hosting.JavaScript").GetString());
    }

    [Theory]
    [InlineData("stable", true, "AspireNuGetSource")]
    [InlineData("stable", false, "AspireNuGetSource")]
    [InlineData("daily", true, "AspireNuGetSource")]
    [InlineData("daily", false, "AspireNuGetSource")]
    [InlineData("staging", true, "AspireNuGetSource")]
    [InlineData("staging", false, "AspireNuGetSource")]
    [InlineData("pr-12345", true, "AspireNuGetSource")]
    [InlineData("pr-12345", false, "AspireNuGetSource")]
    [InlineData("local", true, "AspireNuGetSource")]
    [InlineData("local", false, "AspireNuGetSource")]
    [InlineData("stable", true, "aspireNugetSource")]
    [InlineData("stable", false, "aspireNugetSource")]
    public async Task LocalTemplate_InjectsIdentityNuGetSourceWithoutPrompting(string channel, bool interactive, string parameterName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateNuGetSourceTemplateAsync(workspace, parameterName);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive);
        var context = workspace.CreateExecutionContext(
            identityChannel: channel,
            identityVersion: "17.0.0-preview.1.12345.1+abcdef123456",
            identityCommit: "abcdef1234567890abcdef1234567890abcdef1234");
        if (channel is "local" or "pr-12345")
        {
            Directory.CreateDirectory(Path.Combine(context.HivesDirectory.FullName, channel, "packages"));
        }

        services.AddSingleton(context);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        await Verify(ReadOutput(workspace))
            .AddScrubber(builder => builder
                .Replace('\\', '/')
                .Replace(PathNormalizer.NormalizePathForStorage(workspace.WorkspaceRoot.FullName), "{workspace}"))
            .UseParameters(channel, interactive, parameterName == "AspireNuGetSource" ? "canonical" : "mixed-case");
    }

    [Theory]
    [InlineData("stable", "17.0.0", true)]
    [InlineData("stable", "17.0.0-preview.1", false)]
    [InlineData("daily", "17.0.0-preview.1.12345.1", true)]
    [InlineData("staging", "17.0.0", false)]
    [InlineData("pr-12345", "17.0.0-pr.12345.gabcdef12", false)]
    [InlineData("local", "17.0.0-dev", true)]
    public async Task LocalTemplate_ConfiguresMissingNuGetConfigForIdentity(string channel, string version, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive);
        const string commit = "abcdef1234567890abcdef1234567890abcdef1234";
        var context = workspace.CreateExecutionContext(
            identityChannel: channel, identityVersion: version, identityCommit: commit);
        if (channel is "local" or "pr-12345")
        {
            context.HivesDirectory.CreateSubdirectory($"{channel}/packages");
        }
        services.AddSingleton(context);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        var outputPath = Path.Combine(workspace.Path, "out");
        var expectedSource = channel switch
        {
            "stable" => PackageSources.NuGetOrg,
            "daily" => "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json",
            "staging" => "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            _ => Path.Combine(context.HivesDirectory.FullName, channel, "packages")
        };
        var expectedSources = channel == "stable"
            ? [expectedSource]
            : new[] { expectedSource, PackageSources.NuGetOrg }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedSources, NuGetTestHelper.GetEligiblePackageSources(outputPath, "Aspire.AppHost.Sdk"), PackageSourceIdentity.Comparer);
        Assert.Equal(expectedSources, NuGetTestHelper.GetEligiblePackageSources(outputPath, "Aspire.Hosting.Redis"), PackageSourceIdentity.Comparer);
        Assert.Equal(expectedSources, NuGetTestHelper.GetEligiblePackageSources(outputPath, "Other.Dependency"));
        await Verify(ReadOutput(workspace))
            .AddScrubber(builder => builder.Replace('\\', '/').Replace(workspace.Path.Replace('\\', '/'), "{workspace}"))
            .ScrubLinesWithReplace(line => Regex.Replace(line, "aspire-apphost-[a-f0-9]{16}", "aspire-apphost-source"))
            .UseParameters(channel, version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_CreatesMissingNuGetConfigForStableSourceOverrides(bool localPackagesOverride)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        var packages = workspace.CreateDirectory("packages & assets");
        const string sourceOverride = "https://nuget.example/v3/index.json";
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: "stable",
            identityVersion: "17.0.0",
            identityPackagesDirectory: localPackagesOverride ? packages : null,
            nugetServiceIndexOverride: sourceOverride));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        var outputPath = Path.Combine(workspace.Path, "out");
        Assert.Equal(
            new[] { localPackagesOverride ? packages.FullName : sourceOverride, PackageSources.NuGetOrg }.Order(StringComparer.Ordinal),
            NuGetTestHelper.GetEligiblePackageSources(outputPath, "Aspire.Hosting"), PackageSourceIdentity.Comparer);
        await Verify(ReadOutput(workspace))
            .AddScrubber(builder => builder.Replace('\\', '/').Replace(workspace.Path.Replace('\\', '/'), "{workspace}"))
            .ScrubLinesWithReplace(line => Regex.Replace(line, "aspire-apphost-[a-f0-9]{16}", "aspire-apphost-source"))
            .UseParameters(localPackagesOverride);
    }

    [Theory]
    [InlineData("stable", "nuget.config")]
    [InlineData("daily", "NuGet.Config")]
    [InlineData("staging", "NuGet.config")]
    [InlineData("pr-12345", "nuget.config")]
    public async Task LocalTemplate_PreservesEnterpriseNuGetConfigAndRouting(string channel, string fileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateNuGetSourceTemplateAsync(workspace);
        var templateConfigPath = Path.Combine(workspace.Path, "template", "nuget.config");
        var targetConfigPath = Path.Combine(workspace.Path, "template", fileName);
        if (fileName != "nuget.config")
        {
            File.Delete(templateConfigPath);
        }
        const string enterpriseSource = "https://enterprise.example/v3/index.json";
        string[] enterprisePackages =
        [
            "Aspire.Hosting.Enterprise.Deployment",
            "Aspire.Provisioning.Enterprise.Deployment",
            "Aspire.Hosting.Enterprise.Defaults",
            "Azure.Provisioning.Enterprise.Defaults",
            "Aspire.Hosting.Azure.EnterpriseGateway"
        ];
        await File.WriteAllTextAsync(targetConfigPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="template-nuget" value="https://api.nuget.org/v3/index.json" />
                <!--#if (separateAspireSource) -->
                <add key="template-aspire" value="AspireNuGetSourceValue" />
                <!--#endif -->
                <add key="enterprise" value="https://enterprise.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="template-nuget">
                  <package pattern="*" />
                </packageSource>
                <!--#if (separateAspireSource) -->
                <packageSource key="template-aspire">
                  <package pattern="Aspire*" />
                </packageSource>
                <!--#endif -->
                <packageSource key="enterprise">
                  <package pattern="Aspire.Hosting.Enterprise.Deployment" />
                  <package pattern="Aspire.Provisioning.Enterprise.Deployment" />
                  <package pattern="Aspire.Hosting.Enterprise.Defaults" />
                  <package pattern="Azure.Provisioning.Enterprise.Defaults" />
                  <package pattern="Aspire.Hosting.Azure.EnterpriseGateway" />
                </packageSource>
              </packageSourceMapping>
              <config>
                <add key="globalPackagesFolder" value=".enterprise-packages" />
              </config>
            </configuration>
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        var context = workspace.CreateExecutionContext(
            identityChannel: channel, identityVersion: "17.0.0",
            identityCommit: "abcdef1234567890abcdef1234567890abcdef1234");
        if (channel is "pr-12345")
        {
            context.HivesDirectory.CreateSubdirectory($"{channel}/packages");
        }
        services.AddSingleton(context);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        var outputPath = Path.Combine(workspace.Path, "out");
        foreach (var package in enterprisePackages)
        {
            Assert.Equal([enterpriseSource], NuGetTestHelper.GetEligiblePackageSources(outputPath, package));
        }
        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(outputPath, "Other.Dependency"));
        var expectedSource = channel switch
        {
            "stable" => "https://stale.example/v3/index.json",
            "daily" => "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json",
            "staging" => "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            _ => Path.Combine(context.HivesDirectory.FullName, channel, "packages")
        };
        Assert.Equal([expectedSource], NuGetTestHelper.GetEligiblePackageSources(outputPath, "Aspire.Hosting.Redis"), PackageSourceIdentity.Comparer);
        await Verify(ReadOutput(workspace))
            .AddScrubber(builder => builder.Replace('\\', '/').Replace(workspace.Path.Replace('\\', '/'), "{workspace}"))
            .UseParameters(channel, fileName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_ConfiguresOnlyGeneratedNuGetConfig(bool templateOwnsConfig)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        const string ownedConfig = "<configuration><!-- Enterprise-owned routing --></configuration>";
        var sourceConfigPath = Path.Combine(workspace.Path, "template", "nuget.config");
        if (templateOwnsConfig)
        {
            await File.WriteAllTextAsync(sourceConfigPath, ownedConfig);
        }
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "daily", identityVersion: "17.0.0-preview.1"));
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(workspace.Path, "template"), "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination.FullName, Path.GetRelativePath(Path.Combine(workspace.Path, "template"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            return Task.CompletedTask;
        });
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-repository https://example.test/templates.git --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(gitCommands.Commands, command => command.Arguments[0] == "fetch");
        if (templateOwnsConfig)
        {
            Assert.Equal(ownedConfig, await File.ReadAllTextAsync(sourceConfigPath));
            Assert.Equal(ownedConfig, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "nuget.config")));
        }
        else
        {
            Assert.Equal([".template.config/aspirecli.host.json", ".template.config/template.json", "aspire.config.json"],
                ReadOutput(Path.Combine(workspace.Path, "template")).Keys);
        }
        // The acquisition directory is atomically moved into the retained cache after cloning.
        var cachedTemplate = Assert.Single(Directory.GetDirectories(Path.Combine(workspace.Path, ".aspire", "cache", "git")));
        Assert.Equal(
            ReadOutput(Path.Combine(workspace.Path, "template")),
            ReadOutput(Path.Combine(cachedTemplate, "repository")));
        await Verify(ReadOutput(workspace))
            .ScrubLinesWithReplace(line => Regex.Replace(line, "aspire-apphost-[a-f0-9]{16}", "aspire-apphost-source"))
            .UseParameters(templateOwnsConfig);
    }

    [Fact]
    public async Task LocalTemplate_NuGetConfigOptInWithMissingChannelFailsBeforeWriting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "pr-missing"));
        services.AddSingleton<IPackagingService>(new TestPackagingService());
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, NewCommandStrings.NoChannelFoundMatching, "pr-missing", "default"),
            Assert.Single(interaction.DisplayedErrors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_FailsWhenRequiredNuGetConfigCannotBeCreated(bool credentialBearingSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        if (!credentialBearingSource)
        {
            var conflictingDirectory = workspace.CreateDirectory("template/nuget.config");
            await File.WriteAllTextAsync(Path.Combine(conflictingDirectory.FullName, "keep.txt"), "Template content");
        }
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "daily", identityVersion: "17.0.0-preview.1"));
        if (credentialBearingSource)
        {
            services.AddSingleton<IPackagingService>(new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                [
                    PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease,
                        [new PackageMapping("Aspire*", "https://user:password@nuget.example/v3/index.json")],
                        new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance)
                ])
            });
        }
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Single(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "out", "nuget.config")));
        if (credentialBearingSource)
        {
            Assert.Equal(
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.LocalTemplateLoadFailed,
                    NewCommandStrings.SourceWithCredentialsCannotBePersisted),
                Assert.Single(interaction.DisplayedErrors));
        }
        else
        {
            Assert.Equal("Template content", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "nuget.config", "keep.txt")));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_InjectsIdentityNuGetSourceOverrides(bool localPackagesOverride)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateNuGetSourceTemplateAsync(workspace);
        var packages = workspace.CreateDirectory("packages & assets");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: "stable",
            identityVersion: "17.0.0",
            identityPackagesDirectory: localPackagesOverride ? packages : null,
            nugetServiceIndexOverride: "https://nuget.example/v3/index.json?first=1&second=2"));
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        var config = XDocument.Load(Path.Combine(workspace.Path, "out", "nuget.config"));
        Assert.Equal(
            localPackagesOverride ? PathNormalizer.NormalizePathForStorage(packages.FullName) : "https://nuget.example/v3/index.json?first=1&second=2",
            config.Root!.Element("packageSources")!.Element("add")!.Attribute("value")!.Value);
        await Verify(ReadOutput(workspace))
            .AddScrubber(builder => builder
                .Replace('\\', '/')
                .Replace(PathNormalizer.NormalizePathForStorage(workspace.WorkspaceRoot.FullName), "{workspace}"))
            .UseParameters(localPackagesOverride);
    }

    [Fact]
    public async Task LocalTemplate_MissingIdentityNuGetSourceFailsBeforeWriting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateNuGetSourceTemplateAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json"), "{}");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "pr-missing"));
        services.AddSingleton<IPackagingService>(new TestPackagingService());
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, NewCommandStrings.NoChannelFoundMatching, "pr-missing", "default"),
            Assert.Single(interaction.DisplayedErrors));
    }

    [Fact]
    public async Task LocalTemplate_UsesSymbolNamesWhenDisplayMetadataIsEmpty()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template", displayNames: false);
        var interaction = new TestInteractionService();
        interaction.PromptForSelectionCallback = (prompt, choices, formatter, _) =>
        {
            Assert.Equal("Mode", prompt);
            Assert.Equal(["api", "worker"], choices.Cast<string>().Select(choice => formatter(choice)));
            return "api";
        };
        interaction.PromptForSelectionsCallback = (prompt, choices, formatter, _) =>
        {
            Assert.Equal("Features", prompt);
            Assert.Equal(["web", "worker"], choices.Cast<string>().Select(choice => formatter(choice)));
            return ["web"];
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("Greeting", Assert.Single(interaction.StringPromptCalls).PromptText);
        Assert.Equal("IncludeMessage", Assert.Single(interaction.BooleanPromptCalls).PromptText);
    }

    [Fact]
    public async Task LocalTemplate_MissingRequiredParameterFailsBeforeWriting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template", requiredGreeting: true);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Equal(string.Format(NewCommandStrings.LocalTemplateCreationFailed, "MissingMandatoryParam", "Greeting"), Assert.Single(interaction.DisplayedErrors));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("file")]
    public async Task LocalTemplate_InvalidDirectoryReportsError(string kind)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        if (kind is "empty" or "malformed")
        {
            workspace.CreateDirectory("template");
        }
        if (kind == "malformed")
        {
            workspace.CreateDirectory("template/.template.config");
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".template.config", "template.json"), "{ not json");
        }
        if (kind == "file")
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template"), "not a directory");
        }
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Theory]
    [InlineData("--source ./feed")]
    [InlineData("--version 1.0.0")]
    [InlineData("--channel daily")]
    [InlineData("--language csharp")]
    public async Task LocalTemplate_RejectsConflictingOptions(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse($"new {options} --template-path template --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(NewCommandStrings.TemplatePathConflictingOptions, Assert.Single(interaction.DisplayedErrors));
    }

    [Fact]
    public async Task LocalTemplate_DoesNotOverwriteNonEmptyOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        workspace.CreateDirectory("out");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "out", "existing.txt"), "Keep me");
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Single(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_PackageWithMultipleTemplatesRequiresSelection(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "templates/first");
        await CreateTemplateAsync(workspace, "templates/second");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive);
        services.AddSingleton<INewCommandPrompter>(new TestNewCommandPrompter(interaction)
        {
            PromptForTemplateCallback = templates =>
            {
                Assert.Equal(["first", "second"], templates.Select(template => template.Name));
                return templates[1];
            }
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("new --template-path templates --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        if (interactive)
        {
            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Empty(interaction.DisplayedErrors);
            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "identity.txt")));
        }
        else
        {
            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            Assert.Equal(NewCommandStrings.LocalTemplateSelectionRequiresInteraction, Assert.Single(interaction.DisplayedErrors));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        }
    }

    [Fact]
    public async Task LocalTemplate_RegistersHelpWithoutLoadingTheDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("new --template-path missing --help").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var newCommand = provider.GetRequiredService<NewCommand>();
        Assert.Equal(NewCommandStrings.TemplatePathOptionDescription, Assert.Single(newCommand.Options, option => option.Name == "--template-path").Description);
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task LocalTemplate_CancellationDoesNotCreateOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var templateProvider = provider.GetRequiredService<ITemplateProvider>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            templateProvider.GetTemplatesAsync(new TemplateSource("template", null, null), new CancellationToken(canceled: true)));

        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Fact]
    public async Task LocalTemplate_IsDiscoveredThroughRegisteredFactories()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var engineFactory = Assert.Single(provider.GetServices<ITemplateFactory>().OfType<TemplateEngineFactory>());
        Assert.Empty(engineFactory.GetTemplates());
        Assert.Empty(await engineFactory.GetInitTemplatesAsync());
        Assert.Empty(await engineFactory.GetTemplatesAsync(null));
        var templates = await provider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(new TemplateSource("template", null, null));

        Assert.Equal("template", Assert.Single(templates).Name);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task LocalTemplate_AbsolutePathDoesNotCloneRepository()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var interaction = new TestInteractionService();
        var gitCommands = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse(
            ["new", "--template-path", Path.Combine(workspace.Path, "template"), "--name", "Demo", "--output", "out", "--suppress-agent-init"])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(gitCommands.Commands);
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace)).ScrubInlineGuids();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepositoryTemplate_ClonesGitRepositoryAndRenders(bool specifyTemplatePath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("repository with spaces");
        var templateDirectory = specifyTemplatePath ? "repository with spaces/templates/first" : "repository with spaces";
        await CreateTemplateAsync(workspace, templateDirectory);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", ".");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Add template");

        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        var extractionRequested = false;
        services.AddSingleton<IBundleService>(new TestBundleService(isBundle: true)
        {
            EnsureExtractedAsyncCallback = _ =>
            {
                extractionRequested = true;
                return Task.CompletedTask;
            }
        });
        var cacheRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git");
        using (var provider = services.BuildServiceProvider())
        {
            List<string> arguments = ["new", "--template-repository", new Uri(repository.FullName).AbsoluteUri,
                "--name", "Demo", "--output", "out", "--suppress-agent-init"];
            if (specifyTemplatePath)
            {
                arguments.AddRange(["--template-path", "templates/first"]);
            }

            var stopwatch = Stopwatch.StartNew();
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Starting repository template generation; nested={specifyTemplatePath}.");
            var exitCode = await provider.GetRequiredService<RootCommand>().Parse(arguments.ToArray()).InvokeAsync().DefaultTimeout();
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Repository template generation returned {exitCode} after {stopwatch.Elapsed}.");

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.True(extractionRequested);
            Assert.Empty(interaction.DisplayedErrors);
            Assert.Empty(interaction.StringPromptCalls);
            var checkout = Assert.Single(Directory.GetDirectories(cacheRoot));
            Assert.True(Directory.Exists(Path.Combine(checkout, ".git")));
            await Verify(ReadOutput(workspace)).ScrubInlineGuids().UseParameters(specifyTemplatePath);
        }

        Assert.Single(Directory.GetFileSystemEntries(cacheRoot));
        Assert.True(Directory.Exists(repository.FullName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RepositoryTemplate_CloneFailureIsReportedAndCleanedUp(int failure)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var exception = failure switch
        {
            0 => new GitCommandException(GitFailure.RepositoryUnavailable, 128),
            1 => new GitCommandException(GitFailure.Unavailable, null),
            2 => new GitCommandException(GitFailure.AuthenticationFailed, 128),
            _ => new GitCommandException(GitFailure.TimedOut, null)
        };
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout((_, _, _, _) => throw exception);

        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using (var provider = services.BuildServiceProvider())
        {
            var exitCode = await provider.GetRequiredService<RootCommand>()
                .Parse("new --template-repository https://example.test/templates.git --template-path template --name Demo --output out --suppress-agent-init")
                .InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.FailedToInstallTemplates, exitCode);
            var expectedError = failure switch
            {
                0 => string.Format(NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitRepositoryUnavailable),
                1 => ErrorStrings.GitUnavailable,
                2 => string.Format(NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitAuthenticationFailed),
                _ => ErrorStrings.GitTimedOut
            };
            Assert.Equal(expectedError, Assert.Single(interaction.DisplayedErrors));
            Assert.Single(gitCommands.Commands, command => command.Arguments[0] == "fetch");
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        }

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("templates/../../../outside")]
    [InlineData("absolute")]
    public async Task RepositoryTemplate_RejectsPathsOutsideRepositoryBeforeCloning(string templatePath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var gitCommands = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using (var provider = services.BuildServiceProvider())
        {
            var exitCode = await provider.GetRequiredService<RootCommand>().Parse(
                ["new", "--template-repository", "https://example.test/templates.git", "--template-path",
                    templatePath == "absolute" ? workspace.Path : templatePath,
                    "--name", "Demo", "--output", "out", "--suppress-agent-init"]).InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            Assert.Equal(NewCommandStrings.TemplateRepositoryPathInvalid, Assert.Single(interaction.DisplayedErrors));
            Assert.Empty(gitCommands.Commands);
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        }

        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Fact]
    public async Task RepositoryTemplate_RejectsTemplatePathThroughSymbolicLink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var linkPath = Path.Combine(workspace.Path, "template-link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, workspace.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }

        var interaction = new TestInteractionService();
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
        {
            Directory.CreateDirectory(Path.Combine(destination.FullName, ".git"));
            // Probe outside the command so its exception handling cannot swallow Assert.Skip.
            Directory.Move(linkPath, Path.Combine(destination.FullName, "template"));

            return Task.CompletedTask;
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using (var provider = services.BuildServiceProvider())
        {
            var exitCode = await provider.GetRequiredService<RootCommand>()
                .Parse("new --template-repository https://example.test/templates.git --template-path template --name Demo --output out --suppress-agent-init")
                .InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            Assert.Equal(NewCommandStrings.TemplateRepositoryPathInvalid, Assert.Single(interaction.DisplayedErrors));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        }

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
        Assert.True(Directory.Exists(workspace.Path));
    }

    [Fact]
    public async Task RepositoryTemplate_CloneCancellationDisposesCheckout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, token) =>
        {
            var gitDirectory = destination.CreateSubdirectory(".git");
            var gitObject = Path.Combine(gitDirectory.FullName, "partial-object");
            await File.WriteAllTextAsync(gitObject, "partial download", token);
            File.SetAttributes(gitObject, FileAttributes.ReadOnly);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using (var provider = services.BuildServiceProvider())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(
                    new TemplateSource("template", "https://example.test/templates.git", null), cancellation.Token));

            Assert.Single(gitCommands.Commands, command => command.Arguments[0] == "fetch");
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        }

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Fact]
    public async Task RepositoryTemplate_UsesCachedCheckoutAndKeepsTemplatesAlive()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        List<string> checkouts = [];
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout(async (repository, destination, sha, token) =>
        {
            Assert.Equal("-repository", repository);
            Assert.Equal(TestGitCommandRunner.DefaultCommitSha, sha);
            checkouts.Add(destination.FullName);
            Directory.CreateDirectory(Path.Combine(destination.FullName, ".git"));
            var readOnlyFile = Path.Combine(destination.FullName, ".git", "read-only-object");
            await File.WriteAllTextAsync(readOnlyFile, "object content", token);
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
            await CreateTemplateAsync(workspace, Path.GetRelativePath(workspace.Path, Path.Combine(destination.FullName, "template")));
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using (var provider = services.BuildServiceProvider())
        {
            var templates = provider.GetRequiredService<ITemplateProvider>();
            var first = Assert.Single(await templates.GetTemplatesAsync(new TemplateSource("template", "-repository", "main")));
            var cacheRoot = Path.Combine(provider.GetRequiredService<CliExecutionContext>().CacheDirectory.FullName, "git");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(cacheRoot));
                File.SetUnixFileMode(cacheRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            Assert.Single(await templates.GetTemplatesAsync(new TemplateSource("./template", "-repository", "v1")));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(cacheRoot));
            }

            Assert.Single(checkouts);
            Assert.Equal(2, gitCommands.Commands.Count(command => command.Arguments[0] == "ls-remote"));
            Assert.All(checkouts, path => Assert.Equal(cacheRoot, Directory.GetParent(path)!.Parent!.FullName));

            var command = provider.GetRequiredService<RootCommand>();
            var parseResult = command.Parse("new --template-repository=-repository --template-path template --name Demo --output out --suppress-agent-init");
            var result = await first.ApplyTemplateAsync(new TemplateInputs { Name = "Demo", Output = "out" }, parseResult, TestContext.Current.CancellationToken);
            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            await Verify(ReadOutput(workspace)).ScrubInlineGuids();
        }

        Assert.All(checkouts, path =>
        {
            Assert.False(Directory.Exists(path));
            Assert.False(File.Exists(TemporaryCacheDirectory.GetLeasePath(Directory.GetParent(path)!.FullName)));
        });
        Assert.Single(Directory.GetFileSystemEntries(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Fact]
    public async Task RepositoryTemplate_ResolvesRefsAndReusesPinnedCheckoutsAcrossInvocations()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("repository");
        await CreateTemplateAsync(workspace, "repository/template");
        var identityFile = Path.Combine(repository.FullName, "template", "identity.txt");
        await File.WriteAllTextAsync(identityFile, "first");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init", "--initial-branch=main");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "config", "tag.gpgsign", "false");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", ".");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "First template");
        var firstSha = (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim();
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "tag", "v1");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "tag", "-a", "annotated", "-m", "Annotated version");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "branch", "feature/templates");
        await File.WriteAllTextAsync(identityFile, "second");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Second template");
        var secondSha = (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim();

        Dictionary<string, Dictionary<string, string>> outputs = [];
        (string Label, string? Reference)[] requests =
        [
            ("default", null),
            ("branch", "main"),
            ("branch-with-slash", "feature/templates"),
            ("tag", "v1"),
            ("annotated-tag", "annotated"),
            ("qualified-tag", "refs/tags/annotated"),
            ("sha", firstSha)
        ];
        var cacheRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git");
        foreach (var (label, reference) in requests)
        {
            await GenerateAsync(label, reference);
        }

        var cachedVersions = Directory.GetDirectories(cacheRoot);
        Assert.Equal(2, cachedVersions.Length);
        foreach (var directory in cachedVersions)
        {
            var sha = (await File.ReadAllTextAsync(Path.Combine(directory, ".git", "shallow"))).Trim();
            Assert.Contains(sha, new[] { firstSha, secondSha });
            Assert.Matches("^[0-9a-f]{16}$", Path.GetFileName(directory));
        }

        await File.WriteAllTextAsync(identityFile, "third");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Move branch");
        await GenerateAsync("moved-branch", "main");
        Assert.Equal(3, Directory.GetFileSystemEntries(cacheRoot).Length);
        await Verify(outputs).ScrubInlineGuids();

        async Task GenerateAsync(string label, string? reference)
        {
            var stopwatch = Stopwatch.StartNew();
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Starting generation '{label}', ref '{reference ?? "HEAD"}'.");
            var interaction = new TestInteractionService();
            interaction.ShowStatusCallback = status =>
                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Generation '{label}' after {stopwatch.Elapsed}: {status}");
            var services = CreateServices(workspace, interaction, interactive: false);
            using var provider = services.BuildServiceProvider();
            var repositoryPath = Path.GetRelativePath(provider.GetRequiredService<CliExecutionContext>().WorkingDirectory.FullName, repository.FullName);
            List<string> arguments = ["new", "--template-repository", repositoryPath,
                "--template-path", "template", "--name", "Demo", "--output", $"out/{label}", "--suppress-agent-init"];
            if (reference is not null)
            {
                arguments.AddRange(["--template-repository-ref", reference]);
            }

            var exitCode = await provider.GetRequiredService<RootCommand>().Parse(arguments.ToArray()).InvokeAsync().DefaultTimeout();
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Generation '{label}' returned {exitCode} after {stopwatch.Elapsed}.");

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Empty(interaction.DisplayedErrors);
            outputs[label] = ReadOutput(Path.Combine(workspace.Path, "out", label));
        }
    }

    [Fact]
    public async Task RepositoryTemplate_CacheIdentityIncludesOnlyRepositoryAndResolvedSha()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, _) =>
        {
            Directory.CreateDirectory(Path.Combine(destination.FullName, ".git"));
            await CreateTemplateAsync(workspace, Path.GetRelativePath(workspace.Path, Path.Combine(destination.FullName, "one")));
            await CreateTemplateAsync(workspace, Path.GetRelativePath(workspace.Path, Path.Combine(destination.FullName, "two")));
        });
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(gitCommands.RunCallback);
        gitCommands.RunCallback = (command, token) =>
        {
            if (command.Arguments[0] == "ls-remote" && command.Arguments[3] == "refs/heads/changed")
            {
                return Task.FromResult($"{new string('a', 40)}\trefs/heads/changed\n");
            }
            return run(command, token);
        };
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();
        var templates = provider.GetRequiredService<ITemplateProvider>();
        TemplateSource[] sources =
        [
            new("one", "repository-a", "main"),
            new("two", "repository-a", "main"),
            new("one", "repository-b", "main"),
            new("one", "repository-a", "changed")
        ];
        foreach (var source in sources)
        {
            Assert.Single(await templates.GetTemplatesAsync(source));
        }

        Assert.Single(await templates.GetTemplatesAsync(new TemplateSource("./one", "repository-a", "same-commit-tag")));
        Assert.Equal(5, gitCommands.Commands.Count(command => command.Arguments[0] == "ls-remote"));
        Assert.Equal(3, gitCommands.Commands.Count(command => command.Arguments[0] == "fetch"));
        var cacheRoot = Path.Combine(provider.GetRequiredService<CliExecutionContext>().CacheDirectory.FullName, "git");
        Assert.Equal(3, Directory.GetFileSystemEntries(cacheRoot).Length);

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
            templates.GetTemplatesAsync(new TemplateSource("missing", "repository-a", "main")));

        Assert.Equal(CliExitCodes.InvalidCommand, exception.ExitCode);
        Assert.Equal(6, gitCommands.Commands.Count(command => command.Arguments[0] == "ls-remote"));
        Assert.Equal(3, gitCommands.Commands.Count(command => command.Arguments[0] == "fetch"));
        Assert.Equal(3, Directory.GetFileSystemEntries(cacheRoot).Length);
    }

    [Fact]
    public async Task RepositoryTemplate_ConcurrentInvocationsPublishOnlyOneCompleteEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloneCount = 0;
        var gitCommands = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, token) =>
        {
            if (Interlocked.Increment(ref cloneCount) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(token).DefaultTimeout();
            Directory.CreateDirectory(Path.Combine(destination.FullName, ".git"));
            await CreateTemplateAsync(workspace, Path.GetRelativePath(workspace.Path, Path.Combine(destination.FullName, "template")));
        });
        var firstServices = CreateServices(workspace, new TestInteractionService(), interactive: false);
        firstServices.AddSingleton<IGitCommandRunner>(gitCommands);
        using var firstProvider = firstServices.BuildServiceProvider();
        var secondServices = CreateServices(workspace, new TestInteractionService(), interactive: false);
        secondServices.AddSingleton<IGitCommandRunner>(gitCommands);
        using var secondProvider = secondServices.BuildServiceProvider();
        var source = new TemplateSource("template", "repository", "main");

        var results = await Task.WhenAll(
            firstProvider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(source),
            secondProvider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(source)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal("template", Assert.Single(result).Name));
        Assert.Equal(2, gitCommands.Commands.Count(command => command.Arguments[0] == "fetch"));
        var cacheRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git");
        var cached = Assert.Single(Directory.GetFileSystemEntries(cacheRoot));
        Assert.True(Directory.Exists(Path.Combine(cached, ".git")));
        Assert.True(File.Exists(Path.Combine(cached, "repository", "template", ".template.config", "template.json")));
        Assert.All(gitCommands.Commands.Where(command => command.Arguments[0] == "fetch"),
            command => Assert.False(Directory.Exists(command.WorkingDirectory.FullName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_RefResolutionFailureDoesNotCreateCache(bool canceled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var gitCommands = new TestGitCommandRunner
        {
            RunCallback = (_, token) =>
            {
                if (canceled)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<string>(token);
                }

                return Task.FromResult(string.Empty);
            }
        };
        var services = CreateServices(workspace, new TestInteractionService(), interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();
        var templateProvider = provider.GetRequiredService<ITemplateProvider>();
        var source = new TemplateSource("template", "repository", "missing");
        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => templateProvider.GetTemplatesAsync(source, cancellation.Token));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() => templateProvider.GetTemplatesAsync(source));
            Assert.Equal(string.Format(NewCommandStrings.TemplateRepositoryRefNotFound, "missing"), exception.Message);
        }

        Assert.Equal("ls-remote", Assert.Single(gitCommands.Commands).Arguments[0]);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Theory]
    [InlineData("--template-repository-ref main")]
    [InlineData("--template-path template --template-repository-ref main")]
    [InlineData("--template-repository repository --template-repository-ref \"\"")]
    [InlineData("--template-repository \"\" --template-repository-ref main")]
    public async Task RepositoryTemplate_RefRequiresRepositoryAndNonemptyValue(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var gitCommands = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse($"new {options} --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(NewCommandStrings.TemplateRepositoryRefRequiresRepository, Assert.Single(interaction.DisplayedErrors));
        Assert.Empty(gitCommands.Commands);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Theory]
    [InlineData("--template-repository \"\"")]
    [InlineData("--template-repository https://example.test/templates.git --template-path \"\"")]
    public async Task RepositoryTemplate_RejectsEmptySourceOptions(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var gitCommands = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"new {options} --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(NewCommandStrings.TemplatePathConflictingOptions, Assert.Single(interaction.DisplayedErrors));
        Assert.Empty(gitCommands.Commands);
    }

    [Theory]
    [InlineData("--source ./packages")]
    [InlineData("--version 1.0.0")]
    [InlineData("--channel daily")]
    [InlineData("--language csharp")]
    public async Task RepositoryTemplate_RejectsConflictingOptions(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"new {options} --template-repository https://example.test/templates.git --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(NewCommandStrings.TemplatePathConflictingOptions, Assert.Single(interaction.DisplayedErrors));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    [Theory]
    [InlineData("--template-path")]
    [InlineData("--template-repository")]
    [InlineData("--template-repository-ref")]
    public void SourceOptions_AreNotInheritedByBuiltInTemplateSubcommands(string option)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateServices(workspace, new TestInteractionService(), interactive: false).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse($"new aspire-starter {option} template-value");

        Assert.NotEmpty(result.Errors);
        Assert.Equal([option, "template-value"], result.UnmatchedTokens);
    }

    [Fact]
    public async Task RepositoryTemplate_HelpDoesNotCloneRepository()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var gitCommands = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(gitCommands);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-repository https://example.test/templates.git --help").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(gitCommands.Commands);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")));
    }

    private static async Task CreateNuGetSourceTemplateAsync(TemporaryWorkspace workspace, string parameterName = "AspireNuGetSource")
    {
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "aspirecli.host.json"),
            """{ "createNuGetConfig": true }""");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), $$"""
            {
              "identity": "Aspire.Local.NuGetSource",
              "name": "NuGet source template",
              "shortName": "nuget-source",
              "symbols": {
                "AspireVersion": {
                  "type": "parameter",
                  "datatype": "string",
                  "replaces": "AspireVersionValue"
                },
                "{{parameterName}}": {
                  "type": "parameter",
                  "datatype": "string",
                  "isRequired": true,
                  "defaultValue": "https://stale.example/v3/index.json"
                },
                "sourceXml": {
                  "type": "derived",
                  "valueSource": "{{parameterName}}",
                  "valueTransform": "xmlEncode",
                  "replaces": "AspireNuGetSourceValue"
                },
                "separateAspireSource": {
                  "type": "computed",
                  "value": "({{parameterName}} != \"https://api.nuget.org/v3/index.json\")"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "nuget.config"), """
            <configuration>
              <packageSources>
                <add key="aspire" value="AspireNuGetSourceValue" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="aspire">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "aspire.config.json"), """
            { "sdk": { "version": "AspireVersionValue" } }
            """);
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, TestInteractionService interaction, bool interactive)
    {
        return CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.GitClientFactory = serviceProvider => ActivatorUtilities.CreateInstance<GitClient>(serviceProvider);
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => interactive
                ? TestHelpers.CreateInteractiveHostEnvironment()
                : TestHelpers.CreateNonInteractiveHostEnvironment();
            options.NewCommandPrompterFactory = _ => new NewCommandPrompter(interaction);
            options.NuGetClientFactory = _ => NuGetTestHelper.CreateClient();
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => throw new InvalidOperationException("Local templates must not require the SDK.")
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                NewProjectAsyncCallback = (_, _, _, _, _) => throw new InvalidOperationException("Local templates must not invoke dotnet new.")
            };
            options.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Local templates must not query package feeds.")
            };
        });
    }

    private static async Task CreateVersionedTemplateAsync(TemporaryWorkspace workspace)
    {
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "nuget.config"), """
            <configuration>
              <packageSources>
                <clear />
                <add key="default" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping><clear /></packageSourceMapping>
            </configuration>
            """);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "aspirecli.host.json"),
            """{ "createNuGetConfig": true }""");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.Versioned",
              "name": "Versioned template",
              "shortName": "versioned",
              "symbols": {
                "AspireVersion": {
                  "type": "parameter",
                  "datatype": "string",
                  "replaces": "AspireVersionValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "aspire.config.json"),
            """{ "sdk": { "version": "AspireVersionValue" } }""");
    }

    private static Dictionary<string, string> ReadOutput(TemporaryWorkspace workspace) =>
        ReadOutput(Path.Combine(workspace.Path, "out"));

    private static Dictionary<string, string> ReadOutput(string directory)
    {
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), File.ReadAllText);
    }

    private static async Task CreateSingleChoiceTemplateAsync(TemporaryWorkspace workspace, bool required, string? defaultValue)
    {
        var config = workspace.CreateDirectory("template/.template.config");
        var defaultProperty = defaultValue is null ? "" : $"\"defaultValue\": {JsonSerializer.Serialize(defaultValue)},";
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), $$"""
            {
              "identity": "Aspire.Local.SingleChoice",
              "name": "Single choice template",
              "shortName": "single-choice",
              "symbols": {
                "Mode": {
                  "type": "parameter", "datatype": "choice", "displayName": "Service kind",
                  "isRequired": {{required.ToString().ToLowerInvariant()}},
                  {{defaultProperty}}
                  "replaces": "ModeValue",
                  "choices": [
                    { "choice": "api", "displayName": "HTTP API" },
                    { "choice": "Skip" }
                  ]
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.txt"), "Mode: [ModeValue]\n");
    }

    private static async Task CreateConditionalTemplateAsync(
        TemporaryWorkspace workspace, string conditionProperty, string condition, bool defaultUseDatabase, bool dependentFirst)
    {
        var config = workspace.CreateDirectory("template/.template.config");
        var symbols = new Dictionary<string, object>();
        var dependent = new Dictionary<string, object>
        {
            ["type"] = "parameter",
            ["datatype"] = "string",
            ["displayName"] = "Connection string",
            ["isRequired"] = true,
            ["replaces"] = "ConnectionStringValue",
            [conditionProperty] = condition
        };
        if (dependentFirst)
        {
            symbols["ConnectionString"] = dependent;
        }
        symbols["UseDatabase"] = new
        {
            type = "parameter",
            datatype = "bool",
            displayName = "Use database",
            defaultValue = defaultUseDatabase.ToString().ToLowerInvariant(),
            replaces = "UseDatabaseValue"
        };
        if (!dependentFirst)
        {
            symbols["ConnectionString"] = dependent;
        }
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), JsonSerializer.Serialize(new
        {
            identity = "Aspire.Local.Conditional",
            name = "Conditional template",
            shortName = "conditional",
            symbols
        }));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.txt"),
            "Database enabled: UseDatabaseValue\nConnection: ConnectionStringValue\n");
    }

    private static async Task CreateTemplateAsync(TemporaryWorkspace workspace, string directory, bool requiredGreeting = false, bool displayNames = true)
    {
        var root = workspace.CreateDirectory(directory);
        var name = root.Name;
        var config = root.CreateSubdirectory(".template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), $$"""
            {
              "$schema": "http://json.schemastore.org/template",
              "author": "Aspire tests",
              "identity": "Aspire.Local.{{name}}",
              "name": "{{name}}",
              "shortName": "{{name}}",
              "sourceName": "TemplateApp",
              "symbols": {
                "Greeting": {
                  "type": "parameter", "datatype": "string", "displayName": "{{(displayNames ? "Greeting text" : "")}}",
                  "defaultValue": "Hello", "isRequired": {{requiredGreeting.ToString().ToLowerInvariant()}}, "replaces": "GreetingValue"
                },
                "IncludeMessage": {
                  "type": "parameter", "datatype": "bool", "displayName": "{{(displayNames ? "Include message" : "")}}", "defaultValue": "true"
                },
                "Mode": {
                  "type": "parameter", "datatype": "choice", "displayName": "{{(displayNames ? "Service kind" : "")}}",
                  "defaultValue": "api", "replaces": "ModeValue",
                  "choices": [ { "choice": "worker" }, { "choice": "api" } ]
                },
                "Features": {
                  "type": "parameter", "datatype": "choice", "displayName": "{{(displayNames ? "Features" : "")}}",
                  "allowMultipleValues": true, "defaultValue": "web", "replaces": "FeaturesValue",
                  "choices": [ { "choice": "worker" }, { "choice": "web" } ]
                },
                "IsWorker": { "type": "computed", "value": "(Mode == \"worker\")" },
                "GeneratedId": { "type": "generated", "generator": "guid", "replaces": "GuidValue" }
              },
              "sources": [
                { "modifiers": [ { "condition": "(!IsWorker)", "exclude": [ "worker-only.txt" ] } ] }
              ],
              "postActions": [
                {
                  "actionId": "3A7C4B45-1F5D-4A30-959A-51B88E82B5D2",
                  "description": "Must not run",
                  "args": { "executable": "must-not-execute", "args": "" },
                  "manualInstructions": [ { "text": "Do not execute post-actions in this flow." } ]
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "TemplateApp.cs"), """
            namespace TemplateApp;
            // GreetingValue
            // Mode: ModeValue
            // Features: FeaturesValue
            // Id: GuidValue
            #if (IncludeMessage)
            // Message included
            #endif
            #if (IsWorker)
            // Worker selected
            #endif
            """);
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "worker-only.txt"), "Worker content");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "identity.txt"), name);
    }
}
