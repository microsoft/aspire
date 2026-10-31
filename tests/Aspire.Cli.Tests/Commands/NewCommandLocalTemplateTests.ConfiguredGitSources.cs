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
using Microsoft.Extensions.Logging.Abstractions;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
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
        interaction.SetupStringPromptResponse("SAMPLE");
        interaction.SetupStringPromptResponse("Welcome");
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            selectionCount++;
            if (selectionCount == 1)
            {
                Assert.Empty(GetGitResolutions(git));
                Assert.Empty(GetGitFetches(git));
                Assert.Equal(NewCommandStrings.SearchForMoreTemplates, formatter(options[^1]));
                return options[^1];
            }

            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
            Assert.Equal(
                ["Sample template (sample) - git-source", "Sample template (sample) - local-source",
                    NewCommandStrings.SearchTemplatesAgain, NewCommandStrings.BackToDefaultTemplates],
                options.Select(formatter));
            Assert.Equal(
                [string.Format(CultureInfo.CurrentCulture, NewCommandStrings.SearchingTemplateSource, "local-source"),
                    NewCommandStrings.ResolvingTemplateRepository, NewCommandStrings.CloningTemplateRepository],
                interaction.ShownStatuses);
            return options[0];
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["local-source"] = Path.Combine(workspace.Path, "local"),
            ["git-source"] = repository
        });

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(2, selectionCount);
        Assert.Equal((repository, "HEAD^{}"), Assert.Single(GetGitResolutions(git)));
        var clone = Assert.Single(GetGitFetches(git));
        Assert.Equal(repository, clone.Repository);
        Assert.Equal(TestGitCommandRunner.DefaultCommitSha, clone.CommitSha);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal([NewCommandStrings.TemplateSearchPrompt, "Greeting text"],
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
        var warning = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            NewCommandStrings.TemplateSourceSearchFailed, "broken", expectedError), warning.Message);
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
        Assert.Empty(interaction.DisplayedMessages);
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ConfiguredGitSources_HelpAndDefaultPickerDoNotAcquireRepositories(bool help, bool useObjectSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService();
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, interaction, interactive: true);
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
            .Parse(help ? "new --help" : "new").InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(help ? CliExitCodes.Success : CliExitCodes.Cancelled, exitCode);
        Assert.Empty(GetGitResolutions(git));
        Assert.Empty(GetGitFetches(git));
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.StringPromptCalls);
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
        interaction.SetupStringPromptResponse("sample");
        interaction.SetupStringPromptResponse("Hello");
        var selections = 0;
        interaction.PromptForSelectionCallback = (_, choices, _, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            return ++selections == 1 ? options[^1] : options[0];
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
        Assert.Equal((repositoryUrl, "HEAD^{}"), Assert.Single(GetGitResolutions(git)));
        Assert.Single(GetGitFetches(git));
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace));
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
            .Select(call => (call.Arguments[5], call.WorkingDirectory, call.Arguments[6])).ToArray();
}
