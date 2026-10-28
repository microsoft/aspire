// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "templateSources": null }""")]
    [InlineData("""{ "templates": { "not-a-source": true } }""")]
    [InlineData("""{ "unrelated": true }""")]
    public async Task ConfiguredSources_AbsentSettingHasNoSources(string json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var settings = provider.GetRequiredService<IConfigurationService>();
        await File.WriteAllTextAsync(settings.GetSettingsFilePath(true), json);

        var sources = await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default);

        Assert.Empty(sources);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredSources_MissingFileHasNoSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var sources = await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default);

        Assert.Empty(sources);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "templateSources": [] }""")]
    [InlineData("""{ "templateSources": "relative" }""")]
    [InlineData("{")]
    public async Task ConfiguredSources_InvalidConfigurationFailsDiscovery(string json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<JsonException>(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));

        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\" \"")]
    [InlineData("\"\"")]
    [InlineData("\"relative/path\"")]
    [InlineData("\"http://example.test/templates.git\"")]
    [InlineData("\"git://example.test/templates.git\"")]
    [InlineData("\"file:///templates\"")]
    [InlineData("\"ext::command\"")]
    [InlineData("\"--upload-pack=command\"")]
    [InlineData("\"git@example.test:templates.git\"")]
    [InlineData("\"https://user:password@example.test/templates\"")]
    [InlineData("\"https://token@example.test/templates\"")]
    [InlineData("\"ssh://git:password@example.test/templates\"")]
    [InlineData("\"https://example.test/templates#main\"")]
    [InlineData("\"https:///templates\"")]
    [InlineData("\"https:example.test/templates\"")]
    [InlineData("\"ssh:///templates\"")]
    [InlineData("\"https://example.test\\\\templates\"")]
    [InlineData("\"https://example.test/templates with spaces\"")]
    [InlineData("\"https://example.test/templates\\n\"")]
    [InlineData("""{ "path": "templates" }""")]
    [InlineData("""{ "repository": null }""")]
    [InlineData("""{ "repository": false }""")]
    [InlineData("""{ "repository": 42 }""")]
    [InlineData("""{ "repository": {} }""")]
    [InlineData("""{ "repository": [] }""")]
    [InlineData("""{ "repository": "" }""")]
    [InlineData("""{ "repository": " " }""")]
    [InlineData("""{ "repository": "/local/repository" }""")]
    [InlineData("""{ "repository": "http://example.test/templates" }""")]
    [InlineData("""{ "repository": "git@example.test:templates" }""")]
    [InlineData("""{ "repository": "https://token@example.test/templates" }""")]
    [InlineData("""{ "repository": "https://example.test/templates?q=search" }""")]
    [InlineData("""{ "repository": "https://example.test/templates#main" }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": 42 }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": {} }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": "" }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": " " }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": "main\n" }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": false }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": [] }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": "" }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": " " }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": "templates\u0000" }""")]
    public async Task ConfiguredSources_InvalidEntryFailsDiscovery(string invalidValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, $$"""
            {
              // The global config permits comments and trailing commas.
              "templateSources": {
                "broken": {{invalidValue}},
                "working": { "path": {{JsonSerializer.Serialize(workspace.Path)}} },
              }
            }
            """);

        var error = await Record.ExceptionAsync(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));

        Assert.True(error is JsonException or TemplateSourceConfigurationException, error?.ToString());
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("""{ "Repository": "https://example.test/templates" }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": null }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": null }""")]
    [InlineData("""{ "repository": "https://example.test/templates", "futureFeature": { "enabled": true } }""")]
    public async Task ConfiguredSources_UsesConfigurationDeserializationConventions(string definition)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, $$"""{"templateSources": { "company": {{definition}} } }""");

        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));

        Assert.Equal("company", source.Key);
        Assert.Equal(new TemplateSource(".", "https://example.test/templates", null), source.Value.Template);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ConfiguredSources_EmptySourceNameIsRejected(string sourceName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { [sourceName] = workspace.Path });

        await Assert.ThrowsAsync<TemplateSourceConfigurationException>(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredSources_UsesGlobalPathsRegardlessOfLocalConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = workspace.Path });
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "aspire.config.json"),
            """{ "templateSources": { "company": "project-override", "extra": "/project-only" } }""");

        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));

        Assert.Equal("company", source.Key);
        Assert.Equal(workspace.Path, source.Value.Template?.Path);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData("ONE", 1)]
    [InlineData("FRIENDLY", 1)]
    [InlineData("missing", 0)]
    public async Task ConfiguredSources_SearchesNamesAndDescriptionsAcrossSources(string query, int expectedCount)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "first", "one", "Friendly template", "First");
        await CreateSearchTemplateAsync(workspace, "second", "two", "Another template", "Second");
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["first-source"] = Path.Combine(workspace.Path, "first"),
            ["second-source"] = Path.Combine(workspace.Path, "second")
        });

        var results = await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync(query, default);

        Assert.Equal(expectedCount, results.Count);
        Assert.Equal(query.Length == 0
            ? ["second-source", "first-source"]
            : expectedCount == 0 ? [] : new[] { "first-source" }, results.Select(result => result.SourceName));
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredSources_SearchesActualDescriptionWithoutChangingDisplayName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "company-api", "Company starter", "Found",
            description: "Uses PostgreSQL");
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["examples"] = Path.Combine(workspace.Path, "template") });

        var result = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("POSTGRESQL", default));

        Assert.Equal("examples", result.SourceName);
        Assert.Equal("company-api", result.Template.Name);
        Assert.Equal("Company starter", result.Template.Description);
        Assert.Equal("Uses PostgreSQL", result.Template.SearchDescription);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredSources_MissingAndEmptySourcesWarnAndContinue()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "valid", "valid", "Valid", "valid");
        var empty = workspace.CreateDirectory("empty");
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["missing"] = Path.Combine(workspace.Path, "missing"),
            ["empty"] = empty.FullName,
            ["working"] = Path.Combine(workspace.Path, "valid")
        });

        var result = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default));

        Assert.Equal("working", result.SourceName);
        Assert.Equal("valid", result.Template.Name);
        Assert.Collection(interaction.DisplayedMessages,
            message =>
            {
                Assert.Equal(KnownEmojis.Warning, message.Emoji);
                Assert.StartsWith(string.Format(CultureInfo.CurrentCulture,
                    NewCommandStrings.TemplateSourceSearchFailed, "missing", ""), message.Message);
            },
            message =>
            {
                Assert.Equal(KnownEmojis.Warning, message.Emoji);
                Assert.StartsWith(string.Format(CultureInfo.CurrentCulture,
                    NewCommandStrings.TemplateSourceSearchFailed, "empty", ""), message.Message);
            });
        Assert.Empty(GetGitFetches(git));
        Assert.Empty(GetGitResolutions(git));
    }

    [Fact]
    public async Task ConfiguredSources_CancellationIsNotReportedAsSourceFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = workspace.Path });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", cancellation.Token));

        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredSources_SelectsExactTemplateWithDuplicateNamesAndPromptsForParameters(bool noBuiltIns)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "first", "same", "Same template", "First");
        await CreateSearchTemplateAsync(workspace, "second", "same", "Same template", "Second");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("");
        interaction.SetupStringPromptResponse("Welcome");
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            selectionCount++;
            if (selectionCount == 1)
            {
                Assert.Equal(NewCommandStrings.SearchForMoreTemplates, formatter(options[^1]));
                return options[^1];
            }

            Assert.Equal(
                ["Same template (same) - first-source", "Same template (same) - second-source",
                    NewCommandStrings.SearchTemplatesAgain, NewCommandStrings.BackToDefaultTemplates],
                options.Select(formatter));
            return options[1];
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        if (noBuiltIns)
        {
            services.RemoveAll<ITemplateFactory>();
            services.AddSingleton<ITemplateFactory, TemplateEngineFactory>();
        }
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["second-source"] = Path.Combine(workspace.Path, "second"),
            ["first-source"] = Path.Combine(workspace.Path, "first")
        });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(2, selectionCount);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal([NewCommandStrings.TemplateSearchPrompt, "Greeting text"],
            interaction.StringPromptCalls.Select(call => call.PromptText));
        await Verify(ReadOutput(workspace)).UseParameters(noBuiltIns);
    }

    [Fact]
    public async Task ConfiguredSources_NoMatchesCanSearchAgainAndGenerate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample", "Found");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("not-found");
        interaction.SetupStringPromptResponse("SAMPLE");
        interaction.SetupStringPromptResponse("Hello");
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            selectionCount++;
            if (selectionCount == 1)
            {
                return options[^1];
            }
            if (selectionCount == 2)
            {
                Assert.Equal([NewCommandStrings.SearchTemplatesAgain, NewCommandStrings.BackToDefaultTemplates],
                    options.Select(formatter));
            }
            return options[0];
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = Path.Combine(workspace.Path, "template") });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(3, selectionCount);
        Assert.Equal(NewCommandStrings.NoMatchingConfiguredTemplates, Assert.Single(interaction.DisplayedMessages).Message);
        Assert.Equal([false, false, true], interaction.StringPromptLogResponses);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredSources_EmptyOrFailedSearchCanReturnWithoutWritingFiles(bool brokenSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("not-found");
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            selectionCount++;
            var options = choices.Cast<object>().ToArray();
            if (selectionCount == 1)
            {
                return options[^1];
            }
            if (selectionCount == 2)
            {
                Assert.Equal(NewCommandStrings.BackToDefaultTemplates, formatter(options[^1]));
                return options[^1];
            }
            Assert.Equal(NewCommandStrings.SearchForMoreTemplates, formatter(options[^1]));
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        if (brokenSource)
        {
            await WriteConfiguredSourcesAsync(provider, new() { ["missing"] = Path.Combine(workspace.Path, "missing") });
        }
        else
        {
            await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample", "Unused");
            await WriteConfiguredSourcesAsync(provider, new() { ["company"] = Path.Combine(workspace.Path, "template") });
        }

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Cancelled, exitCode);
        Assert.Equal(3, selectionCount);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Equal(brokenSource ? KnownEmojis.Warning : KnownEmojis.Information, interaction.DisplayedMessages[0].Emoji);
        Assert.Equal(NewCommandStrings.NoMatchingConfiguredTemplates, interaction.DisplayedMessages[^1].Message);
    }

    [Fact]
    public async Task ConfiguredSources_HelpDoesNotReadSourceConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        await File.WriteAllTextAsync(provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true),
            """{ "templateSources": "invalid" }""");

        Assert.Equal(CliExitCodes.Success, await command.Parse("new --help").InvokeAsync().DefaultTimeout());
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("{}", false)]
    [InlineData("""{"templateSources":{}}""", false)]
    [InlineData("""{"unrelated":true}""", false)]
    [InlineData("""{"templateSources":{"unfinished":{"ref":"main"}}}""", true)]
    [InlineData("""{"templateSources":"invalid"}""", true)]
    [InlineData("{", true)]
    public async Task ConfiguredSources_InitialPickerRejectsMalformedConfigurationOrHidesSearchWithoutSources(string? globalSettings, bool malformed)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var settings = provider.GetRequiredService<IConfigurationService>();
        if (globalSettings is not null)
        {
            await File.WriteAllTextAsync(settings.GetSettingsFilePath(true), globalSettings);
        }
        var localPath = settings.GetSettingsFilePath(false);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, """{"templateSources":{"project-only":{"repository":"https://example.test/templates"}}}""");
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, _, _) =>
        {
            selectionCount++;
            var options = choices.Cast<object>().ToArray();
            Assert.NotEmpty(options);
            Assert.All(options, option => Assert.IsAssignableFrom<ITemplate>(option));
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        var result = await provider.GetRequiredService<RootCommand>().Parse("new")
            .InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(malformed ? CliExitCodes.InvalidCommand : CliExitCodes.Cancelled, result);
        Assert.Equal(malformed ? 0 : 1, selectionCount);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.ShownStatuses);
        if (malformed)
        {
            Assert.Single(interaction.DisplayedErrors);
            Assert.Equal(KnownEmojis.PageFacingUp, Assert.Single(interaction.DisplayedMessages).Emoji);
        }
        else
        {
            Assert.Empty(interaction.DisplayedMessages);
        }
    }

    [Fact]
    public async Task ConfiguredSources_NoSourcesOrBuiltInsReportsNoTemplatesWithoutPrompting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        interaction.PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("There are no template choices.");
        var services = CreateServices(workspace, interaction, interactive: true);
        services.RemoveAll<ITemplateFactory>();
        services.AddSingleton<ITemplateFactory, TemplateEngineFactory>();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse("new").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(["No templates are available for the current environment."], interaction.DisplayedErrors);
        Assert.Empty(interaction.ShownStatuses);
        Assert.Empty(interaction.StringPromptCalls);
    }

    [Fact]
    public async Task ConfiguredSources_ConfigSetGlobalRegistersDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["config", "set", "templateSources.company.path", workspace.Path, "--global"])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Equal("company", source.Key);
        Assert.Equal(workspace.Path, source.Value.Template?.Path);
    }

    [Theory]
    [InlineData("--source", "https://example.test/v3/index.json")]
    [InlineData("--version", "1.0.0")]
    [InlineData("--channel", "stable")]
    [InlineData("--language", "csharp")]
    public async Task ConfiguredSources_ExplicitBuiltInOptionsDoNotOfferConfiguredTemplates(string option, string value)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample", "Found");
        var interaction = new TestInteractionService();
        using var cancellation = new CancellationTokenSource();
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, _, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            Assert.NotEmpty(options);
            Assert.All(options, choice => Assert.IsAssignableFrom<ITemplate>(choice));
            selectionCount++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = Path.Combine(workspace.Path, "template") });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["new", "--name", "Demo", "--output", "out", "--suppress-agent-init", option, value])
            .InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Cancelled, exitCode);
        Assert.Equal(1, selectionCount);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Fact]
    public async Task ConfiguredSources_UnsupportedLanguageFailsWithoutOpeningUnusablePicker()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        interaction.PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("No usable choices.");
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = workspace.Path });

        var result = await provider.GetRequiredService<RootCommand>().Parse("new --language unknown").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(["No templates are available for the current environment."], interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
    }

    [Fact]
    public async Task ConfiguredSources_DuplicateNamesWithinSourceHaveUniquePickerLabels()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "templates/first", "same", "Same template", "First");
        await CreateSearchTemplateAsync(workspace, "templates/second", "same", "Same template", "Second");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("");
        using var cancellation = new CancellationTokenSource();
        var selections = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            if (++selections == 1)
            {
                return options[^1];
            }
            var labels = options.Select(formatter).ToArray();
            Assert.Equal(
                ["Same template (same) - company", "Same template (same) - company [[2]]",
                    NewCommandStrings.SearchTemplatesAgain, NewCommandStrings.BackToDefaultTemplates], labels);
            Assert.Equal(labels.Length, labels.ToDictionary(label => label).Count);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = Path.Combine(workspace.Path, "templates") });

        var result = await provider.GetRequiredService<RootCommand>().Parse("new")
            .InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Cancelled, result);
        Assert.Equal(2, selections);
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task ConfiguredSources_RepeatedQueriesReuseTemplateEngineSession()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample template", "Found");
        using var provider = CreateServices(workspace, new TestInteractionService(), interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = Path.Combine(workspace.Path, "template") });
        var search = provider.GetRequiredService<ConfiguredTemplateSourceService>();

        var first = Assert.Single(await search.SearchAsync("sample", default));
        var second = Assert.Single(await search.SearchAsync("", default));

        Assert.Same(first.Template, second.Template);
    }

    private static Task WriteConfiguredSourcesAsync(IServiceProvider provider, Dictionary<string, object> sources) =>
        File.WriteAllTextAsync(provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true),
            JsonSerializer.Serialize(new
            {
                // Strings are fixture shorthand only; persisted sources always use objects.
                templateSources = sources.ToDictionary(entry => entry.Key, entry => entry.Value is string text
                    ? Path.IsPathFullyQualified(text) ? new { path = text } : (object)new { repository = text }
                    : entry.Value)
            }));

    private static async Task CreateSearchTemplateAsync(
        TemporaryWorkspace workspace, string directory, string shortName, string displayName, string marker, string? description = null)
    {
        var config = workspace.CreateDirectory($"{directory}/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), JsonSerializer.Serialize(new
        {
            identity = $"Aspire.Search.{directory}",
            name = displayName,
            shortName,
            description,
            sourceName = "TemplateApp",
            symbols = new
            {
                Greeting = new
                {
                    type = "parameter",
                    datatype = "string",
                    displayName = "Greeting text",
                    defaultValue = "Hello",
                    replaces = "GreetingValue"
                }
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, directory, "TemplateApp.txt"), $"{marker}: GreetingValue");
    }
}
