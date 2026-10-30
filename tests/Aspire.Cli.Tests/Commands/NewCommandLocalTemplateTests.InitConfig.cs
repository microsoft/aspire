// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData("new --help")]
    [InlineData("template source list --help")]
    public async Task InitConfigSources_HelpDoesNotAcquireConfiguration(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => throw new InvalidOperationException("Help must not acquire init configuration.")
        };
        using var provider = CreateInheritedTemplateServices(workspace, new TestInteractionService(), git, interactive: true).BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(commandLine).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(0, git.Calls);
    }

    [Fact]
    public async Task InitConfigSources_AcquisitionFailureDoesNotReturnGlobalOnlySuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => throw new ConfigSourceException("Configuration source unavailable.")
        };
        using var provider = CreateInheritedTemplateServices(workspace, interaction, git, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["personal"] = workspace.Path });
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        var original = await File.ReadAllTextAsync(path);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("template source list --format json --use-config-source true").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal("Configuration source unavailable.", Assert.Single(interaction.DisplayedErrors));
        Assert.Empty(interaction.DisplayedRawText);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InitConfigSources_GlobalDefinitionsOverrideWholeInheritedSourceAndManagementStaysGlobal()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", JsonNode.Parse("""
                {
                  "templateSources": {
                    "enterprise": { "repository": "https://git.example/enterprise", "ref": "inherited", "path": "templates" },
                    "shared": { "repository": "https://git.example/shared", "ref": "shared-preview" }
                  }
                }
                """)!.AsObject()))
        };
        using var provider = CreateInheritedTemplateServices(workspace, interaction, git, interactive: true).BuildServiceProvider();
        var settings = provider.GetRequiredService<IConfigurationService>();
        var localPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await WriteConfiguredSourcesAsync(provider, new() { ["Enterprise"] = workspace.Path });
        var original = await File.ReadAllTextAsync(settings.GetSettingsFilePath(true));
        var discovery = provider.GetRequiredService<ConfiguredTemplateSourceService>();
        var store = provider.GetRequiredService<TemplateSourceStore>();

        var sources = await discovery.GetSourcesAsync(default);
        var listed = await store.ListAsync(new ConfigSource
        {
            Repository = "https://git.example/configuration",
            Path = AspireConfigFile.FileName,
            Ref = "config-preview"
        }, default);

        Assert.Equal(["Enterprise", "shared"], sources.Select(source => source.Key).Order(StringComparer.Ordinal));
        Assert.Equal(new TemplateSource(workspace.Path, null, null), sources.Single(source => source.Key == "Enterprise").Value.Template);
        Assert.Equal(new TemplateSource(".", "https://git.example/shared", "shared-preview"),
            sources.Single(source => source.Key == "shared").Value.Template);
        Assert.Equal(["Enterprise", "shared"], listed.Select(source => source.Key).Order(StringComparer.Ordinal));
        Assert.Equal(original, await File.ReadAllTextAsync(settings.GetSettingsFilePath(true)));
        Assert.False(await store.RemoveAsync("shared", default));
        Assert.False(File.Exists(localPath));

        await store.AddAsync("SHARED", new JsonObject { ["path"] = workspace.Path }, default);
        Assert.Equal(new TemplateSource(workspace.Path, null, null),
            (await discovery.GetSourcesAsync(default)).Single(source => source.Key == "SHARED").Value.Template);
        Assert.True(await store.RemoveAsync("shared", default));
        Assert.Equal("shared-preview",
            (await discovery.GetSourcesAsync(default)).Single(source => source.Key == "shared").Value.Template?.RepositoryRef);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original),
            JsonNode.Parse(await File.ReadAllTextAsync(settings.GetSettingsFilePath(true)))));
        Assert.Equal(1, git.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitConfigSources_SearchAndGenerateWithGlobalSourcesWithoutPersistingInheritedSources(bool materialize)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "enterprise", "enterprise", "Enterprise", "Enterprise");
        await CreateSearchTemplateAsync(workspace, "personal", "personal", "Personal", "Personal");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("");
        interaction.SetupStringPromptResponse("Hello");
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            if (++selectionCount == 1)
            {
                return options[^1];
            }
            Assert.Equal(
                ["Enterprise (enterprise) - enterprise", "Personal (personal) - personal",
                    Aspire.Cli.Resources.NewCommandStrings.SearchTemplatesAgain, Aspire.Cli.Resources.NewCommandStrings.BackToDefaultTemplates],
                options.Select(formatter));
            return options[0];
        };
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", JsonNode.Parse($$"""
                {
                  "templateSources": { "enterprise": { "path": {{JsonSerializer.Serialize(Path.Combine(workspace.Path, "enterprise"))}} } },
                  "docs": { "llmsTxtUrl": "https://docs.example/llms.txt" }
                }
                """)!.AsObject()))
        };
        using var provider = CreateInheritedTemplateServices(workspace, interaction, git, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["personal"] = Path.Combine(workspace.Path, "personal") });
        var globalPath = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        var originalGlobal = await File.ReadAllTextAsync(globalPath);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"new --use-config-source {materialize.ToString().ToLowerInvariant()} --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(2, selectionCount);
        Assert.Equal(1, git.Calls);
        Assert.Equal(originalGlobal, await File.ReadAllTextAsync(globalPath));
        var projectConfig = Path.Combine(workspace.Path, "out", AspireConfigFile.FileName);
        if (materialize)
        {
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(projectConfig))!.AsObject();
            Assert.Equal(["configSource", "docs"], configuration.Select(property => property.Key).Order(StringComparer.Ordinal));
            Assert.Equal("https://docs.example/llms.txt", configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        }
        else
        {
            Assert.False(File.Exists(projectConfig));
        }
        Assert.Equal("Enterprise: Hello", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "Demo.txt")));
    }

    [Fact]
    public async Task InitConfigSources_ListJsonIncludesInheritedSourcesWithoutSavingConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var localPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var interaction = new TestInteractionService();
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", JsonNode.Parse("""
                {"templateSources":{"enterprise":{"repository":"https://git.example/templates","ref":"preview","future":true}}}
                """)!.AsObject()))
        };
        using var provider = CreateInheritedTemplateServices(workspace, interaction, git, interactive: true).BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("template source list --format json --use-config-source true").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(Assert.Single(interaction.DisplayedRawText).Text, "json");
        var settings = provider.GetRequiredService<IConfigurationService>();
        Assert.False(File.Exists(settings.GetSettingsFilePath(true)));
        Assert.False(File.Exists(localPath));
        Assert.Equal(1, git.Calls);
    }

    private IServiceCollection CreateInheritedTemplateServices(
        TemporaryWorkspace workspace, TestInteractionService interaction, TestConfigSourceRepository git, bool interactive)
    {
        var services = CreateServices(workspace, interaction, interactive);
        services.AddSingleton<IGitClient>(git);
        git.ReadConfigurationWhenUnattended = true;
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = "https://git.example/configuration",
            [CliConfigNames.InitSourceRef] = "config-preview"
        }));
        return services;
    }
}
