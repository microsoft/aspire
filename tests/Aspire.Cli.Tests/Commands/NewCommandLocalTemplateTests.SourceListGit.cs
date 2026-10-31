// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    private const string RealGitTemplateSourceConfiguration = """
        {
          "docs": { "llmsTxtUrl": "https://docs.example/must-not-be-materialized" },
          "templateSources": {
            "git-proof": { "repository": "https://git.example/proof-templates", "ref": "templates/preview", "path": "templates/proof" },
            "personal": { "repository": "https://git.example/overridden", "ref": "must-not-leak", "path": "inherited" }
          }
        }
        """;

    [Theory]
    [InlineData(true, null, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, null, true, false)]
    [InlineData(false, null, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, null, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, null, false, true)]
    public async Task TemplateSourceList_RealGitAcquisitionFollowsConsentWithoutPersistingSettings(
        bool json, bool? useSource, bool accept, bool priorCache)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, commit, version) = await CreateTemplateSourceConfigurationRepositoryAsync(workspace);
        var git = new TestGitCommandRunner();
        var prompts = 0;
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (prompt, choices, _, _) =>
            {
                prompts++;
                Assert.False(json);
                Assert.Equal(ConfigSourceStrings.UseSourcePrompt, prompt);
                Assert.Equal([ConfigSourceStrings.Accept, ConfigSourceStrings.Decline], choices.Cast<string>());
                return accept ? ConfigSourceStrings.Accept : ConfigSourceStrings.Decline;
            }
        };

        string? previousPointer = null;
        string? previousCheck = null;
        if (priorCache)
        {
            using var previousProvider = CreateRealGitTemplateSourceServices(
                workspace, new TestInteractionService(), git, source).BuildServiceProvider();
            Assert.Equal(CliExitCodes.Success, await previousProvider.GetRequiredService<RootCommand>()
                .Parse("template source list --format json --use-config-source").InvokeAsync().DefaultTimeout());
            var previousSettings = previousProvider.GetRequiredService<IConfigurationService>();
            Assert.False(File.Exists(previousSettings.GetSettingsFilePath(true)));
            Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
            var previousCache = previousProvider.GetRequiredService<ConfigSourceService>().GetCacheDirectory(source);
            previousPointer = await File.ReadAllTextAsync(Path.Combine(previousCache, "current"));
            previousCheck = await File.ReadAllTextAsync(Path.Combine(previousCache, "check.json"));
            git.Commands.Clear();
            // A fresh invocation must not reacquire a declined source or silently list its
            // retained templates. Making the local upstream unavailable detects both mistakes.
            Directory.Move(source.Repository, source.Repository + "-unavailable");
        }

        using var provider = CreateRealGitTemplateSourceServices(workspace, interaction, git, source).BuildServiceProvider();
        var settings = provider.GetRequiredService<IConfigurationService>();
        var globalPath = settings.GetSettingsFilePath(true);
        var projectPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        const string globalSettings = """
            {
              "unrelated": "preserve global bytes",
              "templateSources": { "Personal": { "path": "./personal" } }
            }
            """;
        const string projectSettings = """
            {
              "unrelated": "preserve project bytes",
              "templateSources": { "project-only": { "repository": "https://git.example/project-only" } }
            }
            """;
        await File.WriteAllTextAsync(globalPath, globalSettings);
        await File.WriteAllTextAsync(projectPath, projectSettings);
        var globalBytes = await File.ReadAllBytesAsync(globalPath);
        var projectBytes = await File.ReadAllBytesAsync(projectPath);
        var cache = provider.GetRequiredService<ConfigSourceService>().GetCacheDirectory(source);
        Assert.Equal(priorCache, Directory.Exists(cache));
        var options = json ? " --format json" : "";
        if (useSource is not null)
        {
            options += useSource.Value ? " --use-config-source" : " --use-config-source false";
        }

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse($"template source list{options}").InvokeAsync().DefaultTimeout();

        var shouldPrompt = !json && useSource is null;
        var acquired = useSource is true || (shouldPrompt && accept);
        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(shouldPrompt ? 1 : 0, prompts);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedLines);
        Assert.Empty(interaction.DisplayedPlainText);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(globalBytes, await File.ReadAllBytesAsync(globalPath));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectPath));

        if (acquired)
        {
            var mode = json ? GitCommandMode.Unattended : GitCommandMode.Interactive;
            var resolution = Assert.Single(git.Commands, call => call.Arguments[0] == "ls-remote");
            Assert.Equal(mode, resolution.Mode);
            Assert.Equal(source.Repository, resolution.Arguments[2]);
            Assert.Equal($"refs/heads/{source.Ref}", resolution.Arguments[^1]);
            var fetch = Assert.Single(git.Commands, call => call.Arguments[0].StartsWith("--git-dir=", StringComparison.Ordinal)
                && call.Arguments[1] == "fetch");
            Assert.Equal(mode, fetch.Mode);
            Assert.Equal(source.Repository, fetch.Arguments[^2]);
            Assert.Equal($"refs/heads/{source.Ref}", fetch.Arguments[^1]);
            var snapshot = Path.Combine(cache, await File.ReadAllTextAsync(Path.Combine(cache, "current")));
            Assert.True(Directory.Exists(Path.Combine(snapshot, "repository.git")));
            var metadata = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(snapshot, "metadata.json")),
                JsonSourceGenerationContext.Default.ConfigSourceMetadata)!;
            Assert.Equivalent(source, metadata.Source, strict: true);
            Assert.Equal(commit, metadata.Commit);
            Assert.Equal(version, metadata.Version);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(RealGitTemplateSourceConfiguration),
                JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, AspireConfigFile.FileName)))));
        }
        else
        {
            Assert.Empty(git.Commands);
            Assert.Equal(priorCache, Directory.Exists(cache));
            if (priorCache)
            {
                Assert.Equal(previousPointer, await File.ReadAllTextAsync(Path.Combine(cache, "current")));
                Assert.Equal(previousCheck, await File.ReadAllTextAsync(Path.Combine(cache, "check.json")));
            }
            else
            {
                Assert.False(Directory.Exists(Path.GetDirectoryName(cache)));
            }
        }

        if (json)
        {
            Assert.Empty(interaction.DisplayedRenderables);
            Assert.Equal(ConsoleOutput.Error, interaction.Console);
            Assert.All(interaction.DisplayedMessages,
                message => Assert.Equal(ConsoleOutput.Error, message.ConsoleOverride ?? interaction.Console));
            var output = Assert.Single(interaction.DisplayedRawText);
            Assert.Equal(ConsoleOutput.Standard, output.ConsoleOverride);
            using var document = JsonDocument.Parse(output.Text);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            await Verify(output.Text, "json").UseParameters(json, useSource, accept, priorCache);
        }
        else
        {
            Assert.Empty(interaction.DisplayedRawText);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
            console.Profile.Capabilities.Ansi = false;
            console.Profile.Width = 160;
            console.Write(Assert.Single(interaction.DisplayedRenderables));
            await Verify(writer.ToString()).UseParameters(json, useSource, accept, priorCache);
        }
    }

    private IServiceCollection CreateRealGitTemplateSourceServices(
        TemporaryWorkspace workspace, TestInteractionService interaction, TestGitCommandRunner recording, ConfigSource source)
    {
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(provider =>
        {
            var runner = ActivatorUtilities.CreateInstance<GitCommandRunner>(provider);
            // Record the calls without replacing transport: the production runner launches
            // real Git for ref resolution, bare-repository fetch and blob extraction.
            recording.RunCallback = (command, token) =>
                runner.RunAsync(command.WorkingDirectory, command.Arguments, command.Mode, token);
            return recording;
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = source.Repository,
            [CliConfigNames.InitSourcePath] = source.Path,
            [CliConfigNames.InitSourceRef] = source.Ref
        }));

        return services;
    }

    private async Task<(ConfigSource Source, string Commit, string Version)> CreateTemplateSourceConfigurationRepositoryAsync(
        TemporaryWorkspace workspace)
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("configuration source");
        var source = new ConfigSource { Repository = repository.FullName, Path = "teams/platform.json", Ref = "config-preview" };
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init", "--initial-branch=main");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        var path = Path.Combine(repository.FullName, "teams", "platform.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{}");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", "--", source.Path);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Default branch without templates");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "checkout", "-b", source.Ref!);
        await File.WriteAllTextAsync(path, RealGitTemplateSourceConfiguration);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Add proof template sources");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "checkout", "main");
        var runner = new GitCommandRunner(TestHelpers.CreateNonInteractiveHostEnvironment(), NullLogger<GitCommandRunner>.Instance);
        var commit = await runner.RunAsync(repository, ["rev-parse", source.Ref!], GitCommandMode.Captured, TestContext.Current.CancellationToken);
        var version = await runner.RunAsync(repository, ["rev-parse", $"{source.Ref}:{source.Path}"], GitCommandMode.Captured, TestContext.Current.CancellationToken);

        return (source, commit.Trim(), version.Trim());
    }
}
