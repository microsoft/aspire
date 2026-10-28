// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData("""{ "repository": "https://example.test/templates" }""", ".", null)]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": "release/17.0" }""", ".", "release/17.0")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": "templates" }""", "templates", null)]
    [InlineData("""{ "repository": "https://example.test/templates", "path": ".", "ref": "HEAD" }""", ".", "HEAD")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": "templates with spaces", "ref": "v17.0" }""", "templates with spaces", "v17.0")]
    [InlineData("""{ "repository": "https://example.test/templates", "path": "templates", "ref": "refs/tags/v17.0" }""", "templates", "refs/tags/v17.0")]
    [InlineData("""{ "repository": "https://example.test/templates", "ref": "0123456789abcdef0123456789abcdef01234567" }""", ".", TestGitCommandRunner.DefaultCommitSha)]
    public async Task ConfiguredGitSourceObjects_ParsesDefaultsAndSelectors(string json, string expectedPath, string? expectedRef)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = JsonSerializer.Deserialize<JsonElement>(json) });

        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));

        Assert.Equal("company", source.Key);
        Assert.Equal(new TemplateSource(expectedPath, "https://example.test/templates", expectedRef), source.Value.Template);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("https://example.test/templates")]
    [InlineData("ssh://git@example.test/templates.git")]
    public async Task ConfiguredGitSourceObjects_ConfigSetRegistersNestedFieldsWithoutAcquiringRepository(string repository)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var (field, value) in new[] { ("repository", repository), ("ref", "release/17.0"), ("path", "templates") })
        {
            var exitCode = await command.Parse(["config", "set", $"templateSources.company.{field}", value, "--global"])
                .InvokeAsync().DefaultTimeout();
            Assert.Equal(CliExitCodes.Success, exitCode);
        }

        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Equal("company", source.Key);
        Assert.Equal(new TemplateSource("templates", repository, "release/17.0"), source.Value.Template);
        Assert.Empty(GetGitResolutions(git));
        Assert.Empty(GetGitFetches(git));
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredGitSourceObjects_MixedSearchScopesPathsAndSharesCheckout()
    {
        const string repository = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "local", "local", "Local template", "Local");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("");
        interaction.SetupStringPromptResponse("Welcome");
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            var root = Path.GetRelativePath(workspace.Path, destination.FullName);
            await CreateSearchTemplateAsync(workspace, $"{root}/templates/first", "one", "First template", "First");
            await CreateSearchTemplateAsync(workspace, $"{root}/templates/second", "two", "Second template", "Second");
        });
        var selections = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            if (++selections == 1)
            {
                Assert.Empty(GetGitResolutions(git));
                Assert.Empty(GetGitFetches(git));
                return options[^1];
            }

            Assert.Equal(
                ["First template (one) - all", "First template (one) - first",
                    "Local template (local) - local", "Second template (two) - all",
                    "Second template (two) - second", NewCommandStrings.SearchTemplatesAgain,
                    NewCommandStrings.BackToDefaultTemplates], options.Select(formatter));
            return options[4];
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["all"] = repository,
            ["first"] = new { repository, @ref = "release/17.0", path = "templates/first" },
            ["second"] = new { repository, @ref = "v17.0", path = "templates/second" },
            ["local"] = Path.Combine(workspace.Path, "local")
        });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(2, selections);
        Assert.Equal([(repository, "HEAD^{}"), (repository, "refs/heads/release/17.0"), (repository, "refs/heads/v17.0")], GetGitResolutions(git));
        Assert.Equal(TestGitCommandRunner.DefaultCommitSha, Assert.Single(GetGitFetches(git)).CommitSha);
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("templates/../../outside")]
    [InlineData("absolute")]
    public async Task ConfiguredGitSourceObjects_RejectsEscapingPathsBeforeAcquiringRepository(string templatePath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "local", "local", "Local template", "Local");
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["broken"] = new
            {
                repository = "https://example.test/templates",
                path = templatePath == "absolute" ? workspace.Path : templatePath
            },
            ["working"] = Path.Combine(workspace.Path, "local")
        });

        var error = await Assert.ThrowsAsync<TemplateSourceConfigurationException>(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default));

        Assert.Empty(GetGitResolutions(git));
        Assert.Empty(GetGitFetches(git));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            NewCommandStrings.TemplateSourceSettingInvalid,
            "templateSources.broken.path", NewCommandStrings.TemplateRepositoryPathInvalid), error.Message);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredGitSourceObjects_RejectsSymlinkPathAndPreservesOtherSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "local", "local", "Local template", "Local");
        var link = Path.Combine(workspace.Path, "template-link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(workspace.Path, "local"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }

        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            Directory.Move(link, Path.Combine(destination.FullName, "templates"));
            return Task.CompletedTask;
        });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["broken"] = new { repository = "https://example.test/templates", path = "templates" },
            ["working"] = Path.Combine(workspace.Path, "local")
        });

        var result = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default));

        Assert.Equal("working", result.SourceName);
        var warning = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            NewCommandStrings.TemplateSourceSearchFailed, "broken", NewCommandStrings.TemplateRepositoryPathInvalid),
            warning.Message);
        Assert.Single(GetGitFetches(git));
        Assert.Empty(Directory.GetFileSystemEntries(
            Path.Combine(provider.GetRequiredService<CliExecutionContext>().CacheDirectory.FullName, "git")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "local", "TemplateApp.txt")));
    }

    [Fact]
    public async Task ConfiguredGitSourceObjects_MissingPathDoesNotPoisonOtherPathsInSameRepository()
    {
        const string repository = "https://example.test/templates";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            await CreateSearchTemplateAsync(workspace,
                Path.GetRelativePath(workspace.Path, Path.Combine(destination.FullName, "templates/sample")),
                "sample", "Sample template", "Working");
        });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["missing"] = new { repository, path = "templates/missing" },
            ["working"] = new { repository, path = "templates/sample" }
        });

        var result = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default));

        Assert.Equal("working", result.SourceName);
        Assert.Equal("sample", result.Template.Name);
        Assert.Equal(2, GetGitFetches(git).Count);
        var missingPath = Path.Combine(GetGitFetches(git)[0].Destination.FullName, "templates", "missing");
        var warning = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            NewCommandStrings.TemplateSourceSearchFailed, "missing",
            string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateDirectoryNotFound, missingPath)),
            warning.Message);
        Assert.Single(Directory.GetFileSystemEntries(
            Path.Combine(provider.GetRequiredService<CliExecutionContext>().CacheDirectory.FullName, "git")));
    }

    [Fact]
    public async Task ConfiguredGitSourceObjects_MissingRefDoesNotBlockOtherRefs()
    {
        const string repository = "https://example.test/templates";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Working"));
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(git.RunCallback);
        git.RunCallback = (command, token) =>
        {
            if (command.Arguments[0] == "ls-remote" && command.Arguments[3] == "refs/heads/missing")
            {
                throw new GitReferenceException("missing");
            }
            return run(command, token);
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["missing"] = new { repository, @ref = "missing" },
            ["working"] = new { repository, @ref = "v17.0" }
        });

        var result = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default));

        Assert.Equal("working", result.SourceName);
        Assert.Equal([(repository, "refs/heads/missing"), (repository, "refs/heads/v17.0")], GetGitResolutions(git));
        Assert.Single(GetGitFetches(git));
        var warning = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            NewCommandStrings.TemplateSourceSearchFailed, "missing",
            string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryRefNotFound, "missing")),
            warning.Message);
    }

    [Fact]
    public async Task ConfiguredGitSourceObjects_RealGitRefsRenderOnlySelectedSubdirectory()
    {
        const string repositoryUrl = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("repository with spaces");
        await CreateSearchTemplateAsync(workspace, "repository with spaces/templates/sample", "sample", "Sample template", "Pinned");
        await CreateSearchTemplateAsync(workspace, "repository with spaces/templates/sibling", "sibling", "Sibling template", "Sibling");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init", "--initial-branch=main");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "config", "tag.gpgsign", "false");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", ".");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Pinned templates");
        var pinnedSha = (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim();
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "branch", "release/17.0");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "tag", "-a", "v17.0", "-m", "Pinned tag");
        await File.WriteAllTextAsync(Path.Combine(repository.FullName, "templates", "sample", "TemplateApp.txt"), "Latest: GreetingValue");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Latest templates");

        (string Label, string? Reference)[] requests =
        [
            ("default", null),
            ("head", "HEAD"),
            ("branch", "release/17.0"),
            ("tag", "v17.0"),
            ("qualified-tag", "refs/tags/v17.0"),
            ("commit", pinnedSha)
        ];
        Dictionary<string, Dictionary<string, string>> outputs = [];
        foreach (var (label, reference) in requests)
        {
            var interaction = new TestInteractionService();
            interaction.SetupStringPromptResponse("");
            interaction.SetupStringPromptResponse("Hello");
            var selections = 0;
            interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
            {
                var options = choices.Cast<object>().ToArray();
                if (++selections == 1)
                {
                    return options[^1];
                }

                Assert.Equal(["Sample template (sample) - company", NewCommandStrings.SearchTemplatesAgain,
                    NewCommandStrings.BackToDefaultTemplates], options.Select(formatter));
                return options[0];
            };
            var git = new TestGitCommandRunner();
            var services = CreateServices(workspace, interaction, interactive: true);
            services.AddSingleton<IProcessExecutionFactory>(
                new ProcessExecutionFactory(new HostEnvironment(), NullLogger<ProcessExecutionFactory>.Instance));
            services.AddSingleton<IGitCommandRunner>(git);
            using var provider = services.BuildServiceProvider();
            var realGit = ActivatorUtilities.CreateInstance<GitCommandRunner>(provider);
            var fixtureUrl = new Uri(repository.FullName).AbsoluteUri;
            // Resolve/fetch a local fixture through real Git, while configuration still
            // goes through the HTTPS object parser and the normal source-search flow.
            git.RunCallback = (command, token) => realGit.RunAsync(
                command.WorkingDirectory,
                command.Arguments.Select(argument => argument == repositoryUrl ? fixtureUrl : argument).ToArray(),
                command.Mode, token);
            Dictionary<string, object> source = new()
            {
                ["repository"] = repositoryUrl,
                ["path"] = "templates/sample"
            };
            if (reference is not null)
            {
                source["ref"] = reference;
            }
            await WriteConfiguredSourcesAsync(provider, new() { ["company"] = source });

            var exitCode = await provider.GetRequiredService<RootCommand>()
                .Parse(["new", "--name", "Demo", "--output", $"out/{label}", "--suppress-agent-init"])
                .InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            if (label == "commit")
            {
                Assert.Empty(GetGitResolutions(git));
            }
            else
            {
                var expectedPattern = reference is null or "HEAD" || reference.StartsWith("refs/", StringComparison.Ordinal)
                    ? $"{reference ?? "HEAD"}^{{}}"
                    : $"refs/heads/{reference}";
                Assert.Equal((repositoryUrl, expectedPattern), Assert.Single(GetGitResolutions(git)));
            }
            Assert.Empty(interaction.DisplayedMessages);
            Assert.Empty(interaction.DisplayedErrors);
            outputs[label] = ReadOutput(Path.Combine(workspace.Path, "out", label));
        }

        Assert.Equal(2, Directory.GetDirectories(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "git")).Length);
        await Verify(outputs);
    }
}
