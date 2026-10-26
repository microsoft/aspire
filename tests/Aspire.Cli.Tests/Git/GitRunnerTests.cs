// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Git;

public class GitRunnerTests(ITestOutputHelper outputHelper)
{
    private const string CommitSha = TestGitRunner.DefaultCommitSha;

    [Fact]
    public async Task CloneAsync_FetchesExactCommitAndChecksOutDetached()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "clone with spaces"));
        var commands = new TestGitCommandRunner();
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        await provider.GetRequiredService<IGitRunner>().CloneAsync("-repository with spaces", destination, CommitSha, TestContext.Current.CancellationToken);

        Assert.Collection(commands.Commands,
            command =>
            {
                Assert.Equal(["init", "--quiet", "--object-format=sha1", "--", destination.FullName], command.Arguments);
                Assert.Equal(workspace.Path, command.WorkingDirectory.FullName);
            },
            command =>
            {
                Assert.Equal(["fetch", "--depth", "1", "--no-tags", "--", "-repository with spaces", CommitSha], command.Arguments);
                Assert.Equal(destination.FullName, command.WorkingDirectory.FullName);
            },
            command => Assert.Equal(["checkout", "--detach", "--force", "FETCH_HEAD"], command.Arguments),
            command => Assert.Equal(["rev-parse", "--verify", "HEAD"], command.Arguments));
        Assert.All(commands.Commands, command => Assert.Equal(GitCommandMode.Captured, command.Mode));
    }

    [Theory]
    [InlineData(null, "HEAD", false)]
    [InlineData("feature/templates", "refs/heads/feature/templates", false)]
    [InlineData("v1", "refs/tags/v1", false)]
    [InlineData("v1", "refs/tags/v1", true)]
    [InlineData("refs/heads/feature/templates", "refs/heads/feature/templates", false)]
    [InlineData("refs/tags/v1", "refs/tags/v1", true)]
    public async Task ResolveReferenceAsync_ResolvesExactRefsAndPeelsAnnotatedTags(string? reference, string advertisedRef, bool annotated)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var tagObjectSha = new string('a', 40);
        var commands = new TestGitCommandRunner
        {
            Output = annotated
                ? $"{tagObjectSha}\t{advertisedRef}\n{CommitSha}\t{advertisedRef}^{{}}\n"
                : $"{CommitSha}\t{advertisedRef}\n"
        };
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var resolved = await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("-repository with spaces", reference, TestContext.Current.CancellationToken);

        Assert.Equal(CommitSha, resolved);
        var command = Assert.Single(commands.Commands);
        var requestedRef = reference ?? "HEAD";
        string[] candidates = requestedRef == "HEAD" || requestedRef.StartsWith("refs/", StringComparison.Ordinal)
            ? [$"{requestedRef}^{{}}", requestedRef]
            : [$"refs/heads/{requestedRef}", $"refs/tags/{requestedRef}^{{}}", $"refs/tags/{requestedRef}"];
        Assert.Equal(["ls-remote", "--", "-repository with spaces", .. candidates], command.Arguments);
        Assert.Equal(GitCommandMode.Captured, command.Mode);
    }

    [Fact]
    public async Task ResolveReferenceAsync_BranchTakesPrecedenceOverTag()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner { Output = $"{new string('a', 40)}\trefs/tags/same^{{}}\n{CommitSha}\trefs/heads/same\n" };
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        Assert.Equal(CommitSha, await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", "same", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha\trefs/heads/missing")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\trefs/heads/different")]
    public async Task ResolveReferenceAsync_MissingOrInvalidRefIsExplicitFailure(string output)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner { Output = output };
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitReferenceException>(() =>
            provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", "missing", TestContext.Current.CancellationToken));

        Assert.Equal("missing", exception.Reference);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task ResolveReferenceAsync_FullShaIsAlreadyResolved(int length)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var resolved = await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", new string('A', length), TestContext.Current.CancellationToken);

        Assert.Equal(new string('a', length), resolved);
        Assert.Empty(commands.Commands);
    }

    [Fact]
    public async Task CloneAsync_RejectsCheckoutOfDifferentCommit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner { Output = new string('a', 40) };
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        await Assert.ThrowsAsync<GitReferenceException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(4, commands.Commands.Count);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("01234567")]
    [InlineData("-upload-pack=unexpected")]
    public async Task CloneAsync_RequiresResolvedCommitSha(string reference)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, reference, TestContext.Current.CancellationToken));

        Assert.Empty(commands.Commands);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CloneAsync_CommandFailureStopsFurtherCommands(int failingCommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        commands.RunCallback = (_, _) => commands.Commands.Count == failingCommand
            ? throw new GitCommandException(GitFailure.CommandFailed, 128)
            : Task.FromResult(CommitSha);
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(128, exception.ExitCode);
        Assert.Equal(failingCommand, commands.Commands.Count);
    }

    [Fact]
    public async Task CloneAsync_AlreadyCanceledDoesNotRunCommands()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        using var provider = CreateServices(workspace, commands).BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, new CancellationToken(canceled: true)));

        Assert.Empty(commands.Commands);
    }

    [Fact]
    public async Task CloneAsync_CallerCancellationIsNotReportedAsTimeout()
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
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, cancellation.Token));

        Assert.Single(commands.Commands);
    }

    [Fact]
    public async Task CloneAsync_TimeoutBudgetCoversAllCommands()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var commands = new TestGitCommandRunner
        {
            RunCallback = (_, token) =>
            {
                time.Advance(TimeSpan.FromSeconds(50));
                token.ThrowIfCancellationRequested();
                return Task.FromResult(CommitSha);
            }
        };
        var services = CreateServices(workspace, commands);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(GitFailure.TimedOut, exception.Failure);
        Assert.Null(exception.ExitCode);
        Assert.Equal(3, commands.Commands.Count);
    }

    [Fact]
    public async Task ResolveReferenceAsync_TimeoutCancelsPendingCommand()
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

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", null, TestContext.Current.CancellationToken));

        Assert.Equal(GitFailure.TimedOut, exception.Failure);
        Assert.Single(commands.Commands);
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, TestGitCommandRunner commands)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IGitCommandRunner>(commands);
        return services;
    }
}
