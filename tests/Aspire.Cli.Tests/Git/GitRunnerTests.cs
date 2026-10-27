// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Git;

public class GitRunnerTests(ITestOutputHelper outputHelper)
{
    private const string CommitSha = TestGitRunner.DefaultCommitSha;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloneAsync_UsesLiteralArgumentsAndDisablesCredentialPrompts(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "clone with spaces"));
        var processFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0, CommitSha)
        };
        List<(string[] Arguments, string WorkingDirectory)> commands = [];
        processFactory.FileNameAssertionCallback = (_, arguments, _, workingDirectory, _) => commands.Add((arguments, workingDirectory.FullName));
        using var provider = CreateServices(workspace, processFactory, interactive).BuildServiceProvider();

        await provider.GetRequiredService<IGitRunner>().CloneAsync("-repository with spaces", destination, CommitSha, TestContext.Current.CancellationToken);

        Assert.Equal("git", processFactory.LastFileName);
        Assert.Collection(commands,
            command =>
            {
                Assert.Equal(["--literal-pathspecs", "init", "--quiet", "--object-format=sha1", "--", destination.FullName], command.Arguments);
                Assert.Equal(workspace.WorkspaceRoot.FullName, command.WorkingDirectory);
            },
            command =>
            {
                Assert.Equal(["--literal-pathspecs", "fetch", "--depth", "1", "--no-tags", "--", "-repository with spaces", CommitSha], command.Arguments);
                Assert.Equal(destination.FullName, command.WorkingDirectory);
            },
            command =>
            {
                Assert.Equal(["--literal-pathspecs", "checkout", "--detach", "--force", "FETCH_HEAD"], command.Arguments);
                Assert.Equal(destination.FullName, command.WorkingDirectory);
            },
            command =>
            {
                Assert.Equal(["--literal-pathspecs", "rev-parse", "--verify", "HEAD"], command.Arguments);
                Assert.Equal(destination.FullName, command.WorkingDirectory);
            });
        Assert.Equal(new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" }, processFactory.LastEnvironmentVariables);
        Assert.True(processFactory.LastProcessInvocationOptions!.SuppressLogging);

        Assert.All(processFactory.CreatedExecutions, execution => Assert.Equal(1, Assert.IsType<TestProcessExecution>(execution).DisposeCount));
    }

    [Theory]
    [InlineData("fatal: repository not found", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: could not resolve host", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: unable to access repository", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: could not read from remote repository", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: Authentication failed", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("Permission denied (publickey).", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("fatal: could not read Username", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("fatal: could not read Password", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("fatal: unexpected failure", nameof(GitFailure.CommandFailed))]
    public async Task CloneAsync_NonzeroExitClassifiesDiagnosticsWithoutExposingPrivateDetails(string diagnostic, string failure)
    {
        var expectedFailure = Enum.Parse<GitFailure>(failure);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, options) =>
            {
                options.StandardErrorCallback?.Invoke($"{diagnostic}: https://user:secret@example.test/private-repository");
                return (128, null);
            }
        };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(128, exception.ExitCode);
        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal(expectedFailure switch
        {
            GitFailure.RepositoryUnavailable => ErrorStrings.GitRepositoryUnavailable,
            GitFailure.AuthenticationFailed => ErrorStrings.GitAuthenticationFailed,
            _ => ErrorStrings.GitCommandFailed
        }, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.True(processFactory.LastProcessInvocationOptions!.SuppressLogging);
        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions)).DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloneAsync_MissingGitReportsSafeStartupFailure(bool failDuringStart)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var startupException = new Win32Exception(2);
        var processFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, _, options) =>
                failDuringStart
                    ? new TestProcessExecution(fileName, arguments, environment, options,
                        (_, _, _) => Task.FromResult((0, (string?)null)), () => 1) { StartException = startupException }
                    : throw startupException
        };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Null(exception.ExitCode);
        Assert.Equal(GitFailure.Unavailable, exception.Failure);
        Assert.Equal(ErrorStrings.GitUnavailable, exception.Message);
        Assert.Null(exception.InnerException);
        if (failDuringStart)
        {
            Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions)).DisposeCount);
        }
        else
        {
            Assert.Empty(processFactory.CreatedExecutions);
        }
    }

    [Fact]
    public async Task CloneAsync_ProcessCannotStartThrowsAndDisposes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, _, options) =>
                new TestProcessExecution(fileName, arguments, environment, options,
                    (_, _, _) => Task.FromResult((0, (string?)null)), () => 1) { StartReturnValue = false }
        };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Null(exception.ExitCode);
        Assert.Null(exception.InnerException);
        var process = Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions));
        Assert.False(process.Started);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task CloneAsync_AlreadyCanceledDoesNotCreateProcess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory();
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, new CancellationToken(canceled: true)));

        Assert.Empty(processFactory.CreatedExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloneAsync_CancellationDisposesProcess(bool processReportsSuccess)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var processFactory = new TestProcessExecutionFactory
        {
            AsyncAttemptCallback = (_, _, token) =>
            {
                Assert.True(token.CanBeCanceled);
                cancellation.Cancel();
                Assert.True(token.IsCancellationRequested);
                return processReportsSuccess
                    ? Task.FromResult((0, (string?)null))
                    : Task.FromCanceled<(int ExitCode, string? Stdout)>(token);
            }
        };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, cancellation.Token));

        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions)).DisposeCount);
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
        var output = annotated
            ? $"{tagObjectSha}\t{advertisedRef}\n{CommitSha}\t{advertisedRef}^{{}}\n"
            : $"{CommitSha}\t{advertisedRef}\n";
        var processFactory = new TestProcessExecutionFactory { AttemptCallback = (_, _) => (0, output) };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        var resolved = await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("-repository with spaces", reference, TestContext.Current.CancellationToken);

        Assert.Equal(CommitSha, resolved);
        var process = Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions));
        Assert.Equal("git", process.FileName);
        var requestedRef = reference ?? "HEAD";
        var candidates = requestedRef == "HEAD" || requestedRef.StartsWith("refs/", StringComparison.Ordinal)
            ? new[] { $"{requestedRef}^{{}}", requestedRef }
            : [$"refs/heads/{requestedRef}", $"refs/tags/{requestedRef}^{{}}", $"refs/tags/{requestedRef}"];
        Assert.Equal(["--literal-pathspecs", "ls-remote", "--", "-repository with spaces", .. candidates], process.Arguments);
        Assert.Equal("0", process.EnvironmentVariables["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task ResolveReferenceAsync_BranchTakesPrecedenceOverTag()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0, $"{new string('a', 40)}\trefs/tags/same^{{}}\n{CommitSha}\trefs/heads/same\n")
        };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        Assert.Equal(CommitSha, await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", "same", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha\trefs/heads/missing")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\trefs/heads/different")]
    public async Task ResolveReferenceAsync_MissingOrInvalidRefIsExplicitFailure(string output)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory { AttemptCallback = (_, _) => (0, output) };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

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
        var processFactory = new TestProcessExecutionFactory();
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        var resolved = await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", new string('A', length), TestContext.Current.CancellationToken);

        Assert.Equal(new string('a', length), resolved);
        Assert.Empty(processFactory.CreatedExecutions);
    }

    [Fact]
    public async Task CloneAsync_RejectsCheckoutOfDifferentCommit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory { AttemptCallback = (_, _) => (0, new string('a', 40)) };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        await Assert.ThrowsAsync<GitReferenceException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(4, processFactory.CreatedExecutions.Count);
        Assert.All(processFactory.CreatedExecutions, process => Assert.Equal(1, Assert.IsType<TestProcessExecution>(process).DisposeCount));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("01234567")]
    [InlineData("-upload-pack=unexpected")]
    public async Task CloneAsync_RequiresResolvedCommitSha(string reference)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory();
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, reference, TestContext.Current.CancellationToken));

        Assert.Empty(processFactory.CreatedExecutions);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CloneAsync_CommandFailureStopsAndDisposesAllProcesses(int failingCommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (attempt, options) =>
            {
                options.StandardErrorCallback?.Invoke("fatal: command failed");
                return (attempt == failingCommand ? 128 : 0, CommitSha);
            }
        };
        using var provider = CreateServices(workspace, processFactory, interactive: false).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(128, exception.ExitCode);
        Assert.Equal(failingCommand, processFactory.CreatedExecutions.Count);
        Assert.All(processFactory.CreatedExecutions, process => Assert.Equal(1, Assert.IsType<TestProcessExecution>(process).DisposeCount));
    }

    [Theory]
    [InlineData("GIT_DIR", true)]
    [InlineData("GIT_WORK_TREE", true)]
    [InlineData("GIT_INDEX_FILE", true)]
    [InlineData("GIT_OBJECT_DIRECTORY", true)]
    [InlineData("GIT_ALTERNATE_OBJECT_DIRECTORIES", true)]
    [InlineData("GIT_COMMON_DIR", true)]
    [InlineData("GIT_NAMESPACE", true)]
    [InlineData("GIT_SHALLOW_FILE", true)]
    [InlineData("git_dir", true)]
    [InlineData("GIT_ASKPASS", false)]
    [InlineData("GIT_SSH_COMMAND", false)]
    [InlineData("GIT_CONFIG_COUNT", false)]
    [InlineData("HOME", false)]
    [InlineData("PATH", false)]
    public async Task ResolveReferenceAsync_FiltersRepositoryEnvironmentButPreservesAuthentication(string name, bool removed)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processFactory = new TestProcessExecutionFactory { AttemptCallback = (_, _) => (0, $"{CommitSha}\tHEAD") };
        using var provider = CreateServices(workspace, processFactory, interactive: true).BuildServiceProvider();

        await provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", null, TestContext.Current.CancellationToken);

        Assert.Equal(removed, processFactory.LastProcessInvocationOptions!.EnvironmentVariableFilter!(name));
    }

    [Fact]
    public async Task CloneAsync_TimeoutBudgetCoversAllCommandsAndDisposesProcesses()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var processFactory = new TestProcessExecutionFactory
        {
            AsyncAttemptCallback = (_, _, token) =>
            {
                time.Advance(TimeSpan.FromSeconds(50));
                token.ThrowIfCancellationRequested();
                return Task.FromResult((0, (string?)CommitSha));
            }
        };
        var services = CreateServices(workspace, processFactory, interactive: true);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().CloneAsync("repository", workspace.WorkspaceRoot, CommitSha, TestContext.Current.CancellationToken));

        Assert.Equal(GitFailure.TimedOut, exception.Failure);
        Assert.Null(exception.ExitCode);
        Assert.Equal(3, processFactory.CreatedExecutions.Count);
        Assert.All(processFactory.CreatedExecutions, process => Assert.Equal(1, Assert.IsType<TestProcessExecution>(process).DisposeCount));
    }

    [Fact]
    public async Task ResolveReferenceAsync_TimeoutCancelsPendingCommand()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var processFactory = new TestProcessExecutionFactory
        {
            AsyncAttemptCallback = async (_, _, token) =>
            {
                time.Advance(TimeSpan.FromMinutes(2));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return (0, (string?)null);
            }
        };
        var services = CreateServices(workspace, processFactory, interactive: false);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync("repository", null, TestContext.Current.CancellationToken));

        Assert.Equal(GitFailure.TimedOut, exception.Failure);
        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions)).DisposeCount);
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, TestProcessExecutionFactory processFactory, bool interactive)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => interactive
                ? TestHelpers.CreateInteractiveHostEnvironment()
                : TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        services.AddSingleton<IProcessExecutionFactory>(processFactory);

        return services;
    }
}
