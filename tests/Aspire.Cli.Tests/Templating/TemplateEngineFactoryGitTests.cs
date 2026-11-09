// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;

namespace Aspire.Cli.Tests.Templating;

public class TemplateEngineFactoryGitTests(ITestOutputHelper outputHelper)
{
    private const string CommitSha = TestGitCommandRunner.DefaultCommitSha;

    [Fact]
    public async Task RepositoryTemplate_AllowsMaskedConsolePromptsDuringResolutionAndFetch()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var console = new TestAnsiConsoleWithInput(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(TextWriter.Null)
        }), new StringReader("y\ny\n"));
        var interaction = new ConsoleInteractionService(new ConsoleEnvironment(console, console, TextReader.Null),
            workspace.CreateExecutionContext(), TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(), NullLoggerFactory.Instance, new ConsoleLogBufferContext());
        var commands = TestGitCommandRunner.CreateForTemplateCheckout(CreateTemplateCheckoutAsync);
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(commands.RunCallback);
        var prompts = 0;
        commands.RunCallback = async (command, token) =>
        {
            if (command.Arguments[0] is "ls-remote" or "fetch")
            {
                Assert.Equal("y", await interaction.PromptForStringAsync("Bound origin",
                    isSecret: true, required: true, logResponse: false, cancellationToken: token));
                prompts++;
            }
            return await run(command, token);
        };
        var services = CreateServices(workspace, commands);
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();
        var templates = await GetFactory(provider).GetTemplatesAsync(
            new TemplateSource(".", "repository", null), TestContext.Current.CancellationToken);
        Assert.Equal("git-template", Assert.Single(templates).Name);
        Assert.Equal(2, prompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_WarnsBeforeAcquisitionIncludingCachedCheckouts(bool cached)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var commands = TestGitCommandRunner.CreateForTemplateCheckout(CreateTemplateCheckoutAsync);
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(commands.RunCallback);
        commands.RunCallback = (command, token) =>
        {
            var warning = Assert.Single(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.PoliceCarLight));
            Assert.Equal(KnownEmojis.PoliceCarLight, warning.Emoji);
            Assert.Equal($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", warning.Message);
            Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride);
            return run(command, token);
        };
        var services = CreateServices(workspace, commands);
        services.AddSingleton<IInteractionService>(interaction);
        var source = new TemplateSource(".", "repository", CommitSha);
        if (cached)
        {
            using var warmup = services.BuildServiceProvider();
            await GetFactory(warmup).GetTemplatesAsync(source, TestContext.Current.CancellationToken);
            interaction.DisplayedMessages.Clear();
        }
        using var provider = services.BuildServiceProvider();
        var factory = GetFactory(provider);

        var templates = await factory.GetTemplatesAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal("git-template", Assert.Single(templates).Name);
        var warning = Assert.Single(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.PoliceCarLight));
        Assert.Equal($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", warning.Message);
        Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task RepositoryTemplate_FetchesExactCommitAndChecksOutDetached(int shaLength)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commit = new string('a', shaLength);
        var commands = TestGitCommandRunner.CreateForTemplateCheckout(CreateTemplateCheckoutAsync);
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var templates = await GetFactory(provider).GetTemplatesAsync(
            new TemplateSource(".", "-repository with spaces", commit), TestContext.Current.CancellationToken);

        Assert.Equal("git-template", Assert.Single(templates).Name);
        var init = commands.Commands.First();
        var destination = init.Arguments[^1];
        Assert.Collection(commands.Commands,
            command =>
            {
                Assert.Equal(["init", "--quiet", $"--object-format={(shaLength == 64 ? "sha256" : "sha1")}", "--", destination], command.Arguments);
                Assert.Equal(workspace.Path, command.WorkingDirectory.FullName);
            },
            command =>
            {
                Assert.Equal(["fetch", "--quiet", "--depth", "1", "--no-tags", "--", "-repository with spaces", commit], command.Arguments);
                Assert.Equal(destination, command.WorkingDirectory.FullName);
            },
            command => Assert.Equal(["checkout", "--quiet", "--detach", "--force", "FETCH_HEAD"], command.Arguments),
            command => Assert.Equal(["rev-parse", "--verify", "HEAD"], command.Arguments));
        Assert.All(commands.Commands, command => Assert.Equal(GitCommandMode.Captured, command.Mode));
    }

    [Fact]
    public async Task RepositoryTemplate_RejectsCheckoutOfDifferentCommit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner { Output = new string('a', 40) };
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
            GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", CommitSha), TestContext.Current.CancellationToken));

        Assert.Equal(string.Format(NewCommandStrings.TemplateRepositoryRefNotFound, CommitSha), exception.Message);
        Assert.Equal(4, commands.Commands.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RepositoryTemplate_CommandFailureStopsFurtherCommands(int failingCommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        commands.RunCallback = (_, _) => commands.Commands.Count == failingCommand
            ? throw new GitCommandException(GitFailure.CommandFailed, 128)
            : Task.FromResult(CommitSha);
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
            GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", CommitSha), TestContext.Current.CancellationToken));

        Assert.Equal(CliExitCodes.FailedToInstallTemplates, exception.ExitCode);
        Assert.Equal(string.Format(NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitCommandFailed), exception.Message);
        Assert.Equal(failingCommand, commands.Commands.Count);
    }

    [Fact]
    public async Task RepositoryTemplate_AlreadyCanceledDoesNotRunCommands()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", CommitSha), new CancellationToken(canceled: true)));

        Assert.Empty(commands.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_CallerCancellationIsNotReportedAsTimeout(bool resolving)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var commands = new TestGitCommandRunner
        {
            RunCallback = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(CommitSha);
            }
        };
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", resolving ? null : CommitSha), cancellation.Token));

        Assert.Single(commands.Commands);
    }

    [Fact]
    public async Task RepositoryTemplate_CheckoutTimeoutBudgetCoversAllCommands()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var commands = new TestGitCommandRunner
        {
            RunCallback = (_, token) =>
            {
                using (GitOperationTimeout.PauseCurrent())
                {
                    time.Advance(TimeSpan.FromMinutes(10));
                    token.ThrowIfCancellationRequested();
                }
                time.Advance(TimeSpan.FromSeconds(40));
                token.ThrowIfCancellationRequested();
                return Task.FromResult(CommitSha);
            }
        };
        var services = CreateServices(workspace, commands);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
            GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", CommitSha), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorStrings.GitTimedOut, exception.Message);
        Assert.Equal(3, commands.Commands.Count);
    }

    [Fact]
    public async Task RepositoryTemplate_ResolutionTimeoutCancelsPendingCommand()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var commands = new TestGitCommandRunner
        {
            RunCallback = async (_, token) =>
            {
                time.Advance(TimeSpan.FromMinutes(2));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return string.Empty;
            }
        };
        var services = CreateServices(workspace, commands);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
            GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", null), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorStrings.GitTimedOut, exception.Message);
        Assert.Single(commands.Commands);
    }

    [Fact]
    public async Task RepositoryTemplate_ResolutionAndCheckoutHaveSeparateTimeoutBudgets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var commands = TestGitCommandRunner.CreateForTemplateCheckout(CreateTemplateCheckoutAsync);
        var run = Assert.IsType<Func<GitCommandInvocation, CancellationToken, Task<string>>>(commands.RunCallback);
        commands.RunCallback = (command, token) =>
        {
            time.Advance(command.Arguments[0] switch
            {
                "ls-remote" => TimeSpan.FromSeconds(119),
                "rev-parse" => TimeSpan.FromSeconds(29),
                _ => TimeSpan.FromSeconds(30)
            });
            return run(command, token);
        };
        var services = CreateServices(workspace, commands);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        var templates = await GetFactory(provider).GetTemplatesAsync(new TemplateSource(".", "repository", null), TestContext.Current.CancellationToken);

        Assert.Equal("git-template", Assert.Single(templates).Name);
        Assert.Equal(5, commands.Commands.Count);
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, TestGitCommandRunner commands)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IGitCommandRunner>(commands);
        return services;
    }

    private static TemplateEngineFactory GetFactory(IServiceProvider provider) =>
        Assert.Single(provider.GetServices<ITemplateFactory>().OfType<TemplateEngineFactory>());

    private static async Task CreateTemplateCheckoutAsync(string repository, DirectoryInfo destination, string commit, CancellationToken token)
    {
        destination.CreateSubdirectory(".git");
        var configuration = destination.CreateSubdirectory(".template.config");
        await File.WriteAllTextAsync(Path.Combine(configuration.FullName, "template.json"),
            """{ "identity": "Aspire.GitTemplate", "name": "Git template", "shortName": "git-template" }""", token);
        await File.WriteAllTextAsync(Path.Combine(destination.FullName, "content.txt"), "template content", token);
    }
}
