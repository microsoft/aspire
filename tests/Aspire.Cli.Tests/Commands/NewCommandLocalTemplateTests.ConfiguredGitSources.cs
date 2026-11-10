// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredGitSources_FetchNoticesIdentifyEachRepository(bool pinned)
    {
        const string firstRepository = "https://example.test/first.git";
        const string secondRepository = "ssh://git@example.test/second.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, choices, _, _) => choices.Cast<object>().Last()
        };
        interaction.SetupStringPromptResponse("Hello");
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var runGit = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(git.RunCallback);
        git.RunCallback = (command, token) =>
        {
            var repository = command.Arguments[0] switch
            {
                "ls-remote" => command.Arguments[2],
                "fetch" => TestGitCommandRunner.GetFetchArguments(command).Repository,
                _ => null
            };
            if (repository is not null)
            {
                var notice = interaction.DisplayedMessages.Last(message => message.Emoji.Equals(KnownEmojis.InboxTray));
                Assert.Contains(repository, notice.Message);
            }
            return runGit(command, token);
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["first"] = new { repository = firstRepository, @ref = pinned ? TestGitCommandRunner.DefaultCommitSha : null },
            ["second"] = new { repository = secondRepository, @ref = pinned ? TestGitCommandRunner.DefaultCommitSha : null }
        });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal([firstRepository, secondRepository], GetGitFetches(git).Select(fetch => fetch.Repository));
        var notices = interaction.DisplayedMessages.Where(message => message.Emoji.Equals(KnownEmojis.InboxTray)).ToArray();
        Assert.Equal(new[] { firstRepository, secondRepository }.Select(repository =>
        {
            var source = pinned ? $"{repository} ({SharedCommandStrings.SourceRef}: {TestGitCommandRunner.DefaultCommitSha})" : repository;
            return $"[blue]{string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.FetchingSourceWithAuthentication,
                string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.GitFetchTemplatesFromSource, source))}[/]";
        }), notices.Select(notice => notice.Message));
        Assert.All(notices, notice => Assert.Equal(ConsoleOutput.Error, notice.ConsoleOverride));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("release/templates", null)]
    [InlineData("refs/heads/release/templates", "Company [templates]")]
    [InlineData(TestGitCommandRunner.DefaultCommitSha, "Company [templates]")]
    public async Task ConfiguredGitSources_PickerShowsConfiguredRepositoryAndRef(string? reference, string? displayName)
    {
        const string repository = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, choices, _, _) => choices.Cast<object>().Last()
        };
        interaction.SetupStringPromptResponse("Hello");
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var runGit = git.RunCallback ?? throw new InvalidOperationException("A template checkout must configure the Git runner.");
        git.RunCallback = (command, token) =>
        {
            var source = reference is null ? repository : $"{repository} ({SharedCommandStrings.SourceRef}: {reference})";
            Assert.Equal(
                [NewCommandStrings.TemplateSourceExplanation, $"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]",
                    $"[blue]{string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.FetchingSourceWithAuthentication,
                        string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.GitFetchTemplatesFromSource, source))}[/]"],
                interaction.DisplayedMessages.Select(message => message.Message));
            Assert.Single(interaction.DisplayedRenderables);
            Assert.Empty(interaction.DisplayedPlainText);
            return runGit(command, token);
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["company"] = new { repository, @ref = reference, displayName }
        });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var choice = Assert.Single(interaction.GroupedSelectionPromptCalls)[^1];
        Assert.Equal(new SelectionChoiceGroup("source:company", displayName ?? "company")
        {
            Style = new Style(Color.Green)
        }, choice.Group);
        Assert.Equal("Sample template", choice.DisplayLabel);
        Assert.Equal("Remote: Hello", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "Demo.txt")));
        Assert.Single(GetGitFetches(git));
    }

    [Fact]
    public async Task ConfiguredGitSources_WarnsOnEachInvocationWithCachedTemplates()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, choices, _, _) => choices.Cast<object>().Last()
        };
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = "https://example.test/templates" });

        for (var invocation = 1; invocation <= 2; invocation++)
        {
            interaction.SetupStringPromptResponse("Hello");
            var result = await provider.GetRequiredService<RootCommand>()
                .Parse($"new --name Demo --output out{invocation} --suppress-agent-init").InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, result);
            var warnings = interaction.DisplayedMessages.Where(
                message => message.Message == $"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]").ToArray();
            Assert.Equal(invocation, warnings.Length);
            Assert.All(warnings, warning => Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride));
            Assert.Equal("Remote: Hello",
                await File.ReadAllTextAsync(Path.Combine(workspace.Path, $"out{invocation}", "Demo.txt")));
        }
        Assert.Single(GetGitFetches(git));
        Assert.Equal(2, GetGitResolutions(git).Count);
    }

    [Theory]
    [InlineData("https://example.test/organization/templates")]
    [InlineData("https://example.test:8443/organization/templates.git")]
    [InlineData("ssh://git@example.test/organization/templates.git")]
    [InlineData("ssh://git@example.test:2222/organization/templates.git")]
    public async Task ConfiguredGitSources_ConfigSetRegistersGitUrlWithoutAcquiringIt(string repository)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["config", "set", "templateSources.company.repository", repository, "--global"])
            .InvokeAsync().DefaultTimeout();
        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("company", source.Key);
        Assert.Equal(new TemplateSource(".", repository, null), source.Value.Template);
        Assert.Empty(GetGitResolutions(git));
        Assert.Empty(GetGitFetches(git));
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task ConfiguredGitSources_MixedSearchSelectsGitTemplateAndShowsAcquisitionStages()
    {
        const string repository = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "local", "sample", "Sample template", "Local");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Welcome");
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var selectionCount = 0;
        ITemplate[] providerTemplates = [];
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            selectionCount++;
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
            Assert.Equal(
                ["Sample template (sample) - git-source", "Sample template (sample) - local-source"],
                options.Select(formatter).TakeLast(2));
            Assert.Equal(
                providerTemplates.Select(template => template.Description),
                options.Select(formatter).SkipLast(2));
            Assert.Equal(
                [NewCommandStrings.ResolvingTemplateRepository, NewCommandStrings.CloningTemplateRepository],
                interaction.ShownStatuses);
            return options[^2];
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        providerTemplates = (await provider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(null, default))
            .Where(template => template.ShowInPrompt)
            .OrderBy(template => template.IsEmpty)
            .ThenBy(template => template.Description, StringComparer.OrdinalIgnoreCase).ToArray();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["local-source"] = Path.Combine(workspace.Path, "local"),
            ["git-source"] = repository
        });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, selectionCount);
        Assert.Equal((repository, "HEAD^{}"), Assert.Single(GetGitResolutions(git)));
        var clone = Assert.Single(GetGitFetches(git));
        Assert.Equal(repository, clone.Repository);
        Assert.Equal(TestGitCommandRunner.DefaultCommitSha, clone.CommitSha);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(["Greeting text"],
            interaction.StringPromptCalls.Select(call => call.PromptText));
        await Verify(ReadOutput(workspace));
    }

    [Fact]
    public async Task ConfiguredGitSources_ReusesCachedCommitsAndKeepsSelectedSnapshotPinned()
    {
        const string repository = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Hello");
        var currentCommit = TestGitCommandRunner.DefaultCommitSha;
        var changedCommit = new string('b', 40);
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, sha, _) =>
            CreateClonedSearchTemplateAsync(
                workspace, destination, sha == TestGitCommandRunner.DefaultCommitSha ? "Original" : "Changed"));
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(git.RunCallback);
        git.RunCallback = (command, token) => command.Arguments[0] == "ls-remote"
            ? Task.FromResult($"{currentCommit}\t{command.Arguments[3]}\n")
            : run(command, token);
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = repository });
        var search = provider.GetRequiredService<ConfiguredTemplateSourceService>();

        var selected = Assert.Single(await search.SearchAsync("sample", default));
        Assert.Same(selected.Template, Assert.Single(await search.SearchAsync("sample", default)).Template);
        Assert.Single(GetGitFetches(git));
        currentCommit = changedCommit;
        Assert.Single(await search.SearchAsync("sample", default));

        Assert.Equal(3, GetGitResolutions(git).Count);
        Assert.Equal([TestGitCommandRunner.DefaultCommitSha, changedCommit], GetGitFetches(git).Select(call => call.CommitSha));
        var command = provider.GetRequiredService<RootCommand>().Parse("new --name Demo --output out --suppress-agent-init");
        var result = await selected.Template.ApplyTemplateAsync(
            new TemplateInputs { Name = "Demo", Output = "out" }, command, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("company", selected.SourceName);
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    public async Task ConfiguredGitSources_FailedRepositoryDoesNotBlockOtherSources(bool failResolution, int failure)
    {
        const string brokenRepository = "https://example.test/broken.git";
        const string workingRepository = "https://example.test/working.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "local", "sample", "Sample template", "Local");
        var interaction = new TestInteractionService();
        var exception = failure switch
        {
            0 => new GitCommandException(GitFailure.RepositoryUnavailable, 128),
            1 => new GitCommandException(GitFailure.Unavailable, null),
            2 => new GitCommandException(GitFailure.AuthenticationFailed, 128),
            3 => new GitCommandException(GitFailure.TimedOut, null),
            _ => (Exception)new GitReferenceException("HEAD")
        };
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (repository, destination, _, token) =>
        {
            if (repository == brokenRepository)
            {
                var partial = destination.CreateSubdirectory(".git");
                await File.WriteAllTextAsync(Path.Combine(partial.FullName, "partial-object"), "partial download", token);
                throw exception;
            }
            await CreateClonedSearchTemplateAsync(workspace, destination, "Remote");
        });
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(git.RunCallback);
        git.RunCallback = (command, token) =>
        {
            if (failResolution && command.Arguments[0] == "ls-remote" && command.Arguments[2] == brokenRepository)
            {
                throw exception;
            }
            return run(command, token);
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["broken"] = brokenRepository,
            ["local-source"] = Path.Combine(workspace.Path, "local"),
            ["git-source"] = workingRepository
        });

        var results = await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default);

        Assert.Equal(["git-source", "local-source"], results.Select(result => result.SourceName));
        var expectedError = failure switch
        {
            0 => string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitRepositoryUnavailable),
            1 => ErrorStrings.GitUnavailable,
            2 => string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitAuthenticationFailed),
            3 => ErrorStrings.GitTimedOut,
            _ => string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryRefNotFound, "HEAD")
        };
        Assert.Collection(interaction.DisplayedMessages.Where(message => !message.Emoji.Equals(KnownEmojis.InboxTray)),
            message => Assert.Equal(NewCommandStrings.TemplateSourceExplanation, message.Message),
            warning =>
            {
                Assert.Equal($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", warning.Message);
                Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride);
            },
            warning =>
            {
                Assert.Equal(KnownEmojis.PoliceCarLight, warning.Emoji);
                Assert.Equal(string.Format(CultureInfo.CurrentCulture,
                    NewCommandStrings.TemplateSourceSearchFailed, "broken", expectedError), warning.Message);
            });
        Assert.Equal([brokenRepository, workingRepository], GetGitResolutions(git).Select(call => call.Repository));
        Assert.Equal(failResolution ? [workingRepository] : new[] { brokenRepository, workingRepository },
            GetGitFetches(git).Select(call => call.Repository));
        Assert.Single(Directory.GetFileSystemEntries(
            Path.Combine(provider.GetRequiredService<CliExecutionContext>().CacheDirectory.FullName, "git")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ConfiguredGitSources_CancellationStopsSearchAndCleansPartialClone(bool cancelResolution, bool useObjectSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, token) =>
        {
            var partial = destination.CreateSubdirectory(".git");
            await File.WriteAllTextAsync(Path.Combine(partial.FullName, "partial-object"), "partial download", token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        });
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(git.RunCallback);
        git.RunCallback = (command, token) =>
        {
            if (cancelResolution && command.Arguments[0] == "ls-remote")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return run(command, token);
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["first"] = useObjectSource
                ? new { repository = "https://example.test/first.git", @ref = "release/17.0", path = "templates" }
                : "https://example.test/first.git",
            ["second"] = "https://example.test/second.git"
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", cancellation.Token));

        Assert.Single(GetGitResolutions(git));
        Assert.Equal(cancelResolution ? 0 : 1, GetGitFetches(git).Count);
        var messages = interaction.DisplayedMessages.Where(message => !message.Emoji.Equals(KnownEmojis.InboxTray)).ToArray();
        Assert.Equal([NewCommandStrings.TemplateSourceExplanation, $"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]"],
            messages.Select(message => message.Message));
        Assert.Equal(ConsoleOutput.Error, messages[1].ConsoleOverride);
        var cacheRoot = Path.Combine(provider.GetRequiredService<CliExecutionContext>().CacheDirectory.FullName, "git");
        if (cancelResolution)
        {
            Assert.False(Directory.Exists(cacheRoot));
        }
        else
        {
            Assert.Empty(Directory.GetFileSystemEntries(cacheRoot));
        }
    }

    [Theory]
    [InlineData("new --help", CliExitCodes.Success, false)]
    [InlineData("new", CliExitCodes.MissingRequiredArgument, false)]
    [InlineData("new aspire-empty --language unknown", CliExitCodes.InvalidCommand, false)]
    [InlineData("new --help", CliExitCodes.Success, true)]
    [InlineData("new", CliExitCodes.MissingRequiredArgument, true)]
    [InlineData("new aspire-empty --language unknown", CliExitCodes.InvalidCommand, true)]
    public async Task ConfiguredGitSources_HelpAndExplicitOrNoninteractiveCommandsDoNotAcquireRepositories(
        string commandLine, int expectedExitCode, bool useObjectSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        const string repository = "https://example.test/templates.git";
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["company"] = useObjectSource ? new { repository, @ref = "release/17.0", path = "templates" } : repository
        });
        interaction.PromptForSelectionCallback = (_, _, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(commandLine).InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        if (expectedExitCode == CliExitCodes.InvalidCommand)
        {
            Assert.Equal(["Template 'aspire-empty' does not support language 'unknown'."], interaction.DisplayedErrors);
            Assert.Equal(KnownEmojis.PageFacingUp, Assert.Single(interaction.DisplayedMessages).Emoji);
        }
        else
        {
            Assert.Empty(interaction.DisplayedMessages);
        }
        Assert.Empty(GetGitResolutions(git));
        Assert.Empty(GetGitFetches(git));
        Assert.Empty(interaction.StringPromptCalls);
    }

    [Fact]
    public async Task ConfiguredGitSources_ExplicitDirectoryTemplateDoesNotDiscoverConfiguredRepositories()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample", "Directory");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Hello");
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = "https://example.test/templates.git" });

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Empty(git.Commands);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal("Directory: Hello", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "Demo.txt")));
    }

    [Fact]
    public async Task ConfiguredGitSources_RealGitCheckoutRendersSelectedTemplate()
    {
        const string repositoryUrl = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("repository with spaces");
        await CreateSearchTemplateAsync(workspace, "repository with spaces/templates/sample", "sample", "Sample template", "Real Git");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", ".");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Add template");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Hello");
        var selections = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            selections++;
            return Assert.Single(options, option => formatter(option) == "Sample template (sample) - company");
        };
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IProcessExecutionFactory>(
            new ProcessExecutionFactory(new HostEnvironment(), NullLogger<ProcessExecutionFactory>.Instance));
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var realGit = ActivatorUtilities.CreateInstance<GitCommandRunner>(provider);
        var fixtureUrl = new Uri(repository.FullName).AbsoluteUri;
        // Exercise real ls-remote/fetch/checkout against a local fixture instead of depending
        // on network access, while the configured source still goes through Git URL parsing.
        git.RunCallback = (command, token) => realGit.RunAsync(
            command.WorkingDirectory,
            command.Arguments.Select(argument => argument == repositoryUrl ? fixtureUrl : argument).ToArray(),
            command.Mode, token);
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = repositoryUrl });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, selections);
        Assert.Equal((repositoryUrl, "HEAD^{}"), Assert.Single(GetGitResolutions(git)));
        Assert.Single(GetGitFetches(git));
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task ConfiguredGitSources_InitialPickerKeepsExactSourceWhenNamesCollideWithBuiltIn(string selectedSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Welcome");
        string shortName = "";
        string displayName = "";
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (repository, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            await CreateSearchTemplateAsync(workspace, Path.GetRelativePath(workspace.Path, destination.FullName),
                shortName, displayName, repository == "https://example.test/first.git" ? "First" : "Second");
        });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var builtIn = (await provider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(null, default))
            .First(template => template.ShowInPrompt);
        shortName = builtIn.Name;
        displayName = builtIn.Description;
        var selections = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            selections++;
            var options = choices.Cast<object>().ToArray();
            Assert.Contains(displayName, options.Select(formatter));
            Assert.Equal(options.Length, options.Select(formatter).Distinct().Count());
            return Assert.Single(options,
                option => formatter(option) == $"{displayName} ({shortName}) - {selectedSource}");
        };
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["first"] = "https://example.test/first.git",
            ["second"] = "https://example.test/second.git"
        });

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(1, selections);
        Assert.Equal(2, GetGitResolutions(git).Count);
        Assert.Equal(2, GetGitFetches(git).Count);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(["Greeting text"], interaction.StringPromptCalls.Select(call => call.PromptText));
        Assert.Equal(selectedSource == "first" ? "First: Welcome" : "Second: Welcome",
            await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "Demo.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredGitSources_InitialPickerWarnsForEmptyAndFailedRepositories(bool noBuiltIns)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((repository, destination, _, _) =>
        {
            if (repository == "https://example.test/broken.git")
            {
                throw new GitCommandException(GitFailure.AuthenticationFailed, 128);
            }
            destination.CreateSubdirectory(".git");
            return Task.CompletedTask;
        });
        var selections = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            selections++;
            var labels = choices.Cast<object>().Select(formatter).ToArray();
            Assert.False(noBuiltIns, "No choices should open a picker when there are no built-ins or discovered templates.");
            Assert.NotEmpty(labels);
            Assert.Equal(6, interaction.DisplayedMessages.Count);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        if (noBuiltIns)
        {
            services.RemoveAll<ITemplateFactory>();
            services.AddSingleton<ITemplateFactory, TemplateEngineFactory>();
        }
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["empty"] = "https://example.test/empty.git",
            ["broken"] = "https://example.test/broken.git"
        });

        var result = await provider.GetRequiredService<RootCommand>().Parse("new --name Demo --output out")
            .InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(noBuiltIns ? CliExitCodes.InvalidCommand : CliExitCodes.Cancelled, result);
        Assert.Equal(noBuiltIns ? 0 : 1, selections);
        Assert.Equal(noBuiltIns ? ["No templates are available for the current environment."] : Array.Empty<string>(), interaction.DisplayedErrors);
        Assert.Equal(2, GetGitFetches(git).Count);
        var messages = interaction.DisplayedMessages.Where(message => !message.Emoji.Equals(KnownEmojis.InboxTray)).ToArray();
        Assert.Equal(noBuiltIns ? 5 : 4, messages.Length);
        Assert.Equal(2, interaction.DisplayedMessages.Count(message => message.Emoji.Equals(KnownEmojis.InboxTray)));
        Assert.Equal(NewCommandStrings.TemplateSourceExplanation, messages[0].Message);
        Assert.Equal($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", messages[1].Message);
        Assert.All(messages.Skip(2).Take(2), message => Assert.Equal(KnownEmojis.PoliceCarLight, message.Emoji));
        if (noBuiltIns)
        {
            Assert.Equal(KnownEmojis.PageFacingUp, messages[^1].Emoji);
        }
        Assert.StartsWith(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed, "empty", ""),
            messages[2].Message);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed, "broken",
            string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitAuthenticationFailed)),
            messages[3].Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    private static async Task CreateClonedSearchTemplateAsync(TemporaryWorkspace workspace, DirectoryInfo destination, string marker)
    {
        destination.CreateSubdirectory(".git");
        await CreateSearchTemplateAsync(workspace,
            Path.GetRelativePath(workspace.Path, destination.FullName), "sample", "Sample template", marker);
    }

    private static IReadOnlyList<(string Repository, string Reference)> GetGitResolutions(TestGitCommandRunner git) =>
        git.Commands.Where(call => call.Arguments[0] == "ls-remote")
            .Select(call => (call.Arguments[2], call.Arguments[3])).ToArray();

    private static IReadOnlyList<(string Repository, DirectoryInfo Destination, string CommitSha)> GetGitFetches(TestGitCommandRunner git) =>
        git.Commands.Where(call => call.Arguments[0] == "fetch")
            .Select(call =>
            {
                var (repository, reference) = TestGitCommandRunner.GetFetchArguments(call);
                return (repository, call.WorkingDirectory, reference);
            }).ToArray();
}
