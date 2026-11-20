// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks fresh history checkout, ordered persistence and command-boundary cancellation.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    [RequiresTools(["bash"])]
    public async Task HistoryCancellationDuringGitIsObservedBeforePush()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_workspace.Path, "fake-git")).FullName;
        var started = Path.Combine(_workspace.Path, "head-started");
        var released = Path.Combine(_workspace.Path, "head-released");
        await WriteExecutableAsync(
            Path.Combine(bin, "git"),
            """
            #!/usr/bin/env bash
            set -eu
            printf '%s\n' "$*" >> git-operations
            case "$*" in
              *"diff --cached --name-only") printf '%s\n' 'causes/test.json' ;;
              *"rev-parse HEAD")
                touch head-started
                while [ ! -f head-released ]; do sleep 0.02; done
                printf '%040d\n' 1
                ;;
            esac
            """);
        using var cancellation = new CancellationTokenSource();
        var command = new AnalyzerCommandExecutor(
            _workspace.Path,
            new Dictionary<string, string>
            {
                ["PATH"] = $"{bin}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            },
            cancellation.Token);
        var store = new CauseHistoryStore(command, "microsoft/aspire", _workspace.Path, new AnalysisDiagnostics());
        var commit = store.CommitAndPushAsync("123", [Path.Combine(_workspace.Path, "causes/test.json")]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!File.Exists(started))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            cancellation.Cancel();
        }
        finally
        {
            await File.WriteAllTextAsync(released, "");
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        Assert.Equal(
            ["--no-pager -C " + _workspace.Path + " add -- causes/test.json",
             "--no-pager -C " + _workspace.Path + " diff --cached --name-only",
             "--no-pager -C " + _workspace.Path + " commit -m Update CI failure analysis for run 123",
             "--no-pager -C " + _workspace.Path + " rev-parse HEAD"],
            await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "git-operations")));
    }

    [Fact]
    [RequiresTools(["git"])]
    public async Task HistoryRejectsExistingCheckoutInsteadOfPublishingFromStaleState()
    {
        await CreateMemoryRemoteAsync(seed: true);
        var directory = Path.Combine(_workspace.Path, "memory checkout");
        var history = new CauseHistoryStore(new AnalyzeCiFailure.AnalyzerCommandExecutor(_workspace.Path), "microsoft/aspire", directory, new AnalysisDiagnostics());
        Assert.True(await history.OpenForReadAsync());

        await Assert.ThrowsAsync<ValidationException>(history.OpenForPublicationAsync);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(directory, "stored-history.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["git"])]
    public async Task MemoryHistoryOpensExistingRemoteBranchWithoutChangingItsContents(bool create)
    {
        await CreateMemoryRemoteAsync(seed: true);
        var directory = Path.Combine(_workspace.Path, "memory checkout");

        var store = new CauseHistoryStore(new AnalyzeCiFailure.AnalyzerCommandExecutor(_workspace.Path), "microsoft/aspire", directory, new AnalysisDiagnostics());
        var opened = true;
        if (create)
        {
            await store.OpenForPublicationAsync();
        }
        else
        {
            opened = await store.OpenForReadAsync();
        }

        Assert.True(opened);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(directory, "stored-history.json")));
        var branch = await RunProcessAsync("git", ["--no-pager", "-C", directory, "symbolic-ref", "--short", "HEAD"]);
        Assert.Equal(0, branch.ExitCode);
        Assert.Equal("memory/ci-failure-analysis", branch.Output.Trim());
        var depth = await RunProcessAsync("git", ["--no-pager", "-C", directory, "rev-list", "--count", "HEAD"]);
        Assert.Equal(0, depth.ExitCode);
        Assert.Equal("1", depth.Output.Trim());
        if (create)
        {
            await AssertPublicationGitConfigurationAsync(directory);
        }
        else
        {
            var identity = await RunProcessAsync("git", ["--no-pager", "-C", directory, "config", "--local", "--get", "user.name"]);
            Assert.Equal(1, identity.ExitCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["git"])]
    public async Task MemoryHistoryCreatesMissingBranchOnlyForPublication(bool create)
    {
        await CreateMemoryRemoteAsync(seed: false);
        var directory = Path.Combine(_workspace.Path, "memory checkout");
        var command = new AnalyzeCiFailure.AnalyzerCommandExecutor(_workspace.Path);

        var store = new CauseHistoryStore(command, "microsoft/aspire", directory, new AnalysisDiagnostics());
        var opened = true;
        if (create)
        {
            await store.OpenForPublicationAsync();
        }
        else
        {
            opened = await store.OpenForReadAsync();
        }

        Assert.Equal(create, opened);
        if (!create)
        {
            Assert.False(Directory.Exists(directory));
            return;
        }

        await AssertPublicationGitConfigurationAsync(directory);
        var branch = await RunProcessAsync("git", ["--no-pager", "-C", directory, "symbolic-ref", "--short", "HEAD"]);
        Assert.Equal(0, branch.ExitCode);
        Assert.Equal("memory/ci-failure-analysis", branch.Output.Trim());
        await File.WriteAllTextAsync(Path.Combine(directory, "--cause.json"), "history");
        await File.WriteAllTextAsync(Path.Combine(directory, "operator-notes.txt"), "unstaged notes");
        var git = new GitRepository(command);
        await git.StageFilesAsync(directory, ["--cause.json"]);
        Assert.Equal("--cause.json\n", (await git.GetStagedFileNamesAsync(directory)).ReplaceLineEndings("\n"));
        await git.CommitAsync(directory, "Seed memory history fixture");
        Assert.Equal("", await git.GetStagedFileNamesAsync(directory));
        await git.PushBranchAsync(directory, "memory/ci-failure-analysis");

        var stored = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote repository.git", "show", "refs/heads/memory/ci-failure-analysis:--cause.json"]);
        Assert.Equal(0, stored.ExitCode);
        Assert.Equal("history", stored.Output);
        var files = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote repository.git", "ls-tree", "--name-only", "memory/ci-failure-analysis"]);
        Assert.Equal(0, files.ExitCode);
        Assert.Equal("--cause.json\n", files.Output.ReplaceLineEndings("\n"));
        Assert.Equal("unstaged notes", await File.ReadAllTextAsync(Path.Combine(directory, "operator-notes.txt")));
    }

    [Theory]
    [InlineData("malformed-reference", "invalid memory branch reference")]
    [InlineData("transport", "transport failed: token=[REDACTED]")]
    [RequiresTools(["bash"])]
    public async Task MemoryHistoryDoesNotInitializeAfterAnUntrustedRemoteLookup(string scenario, string error)
    {
        var fakeBin = Directory.CreateDirectory(Path.Combine(_workspace.Path, "fake-bin")).FullName;
        await WriteExecutableAsync(
            Path.Combine(fakeBin, "git"),
            """
            #!/usr/bin/env bash
            set -eu
            if [ "$1" != --no-pager ]; then exit 99; fi
            case "$*" in
              "--no-pager remote get-url origin") printf '%s\n' 'file:///unused-local-remote.git' ;;
              *" ls-remote --exit-code --heads "*)
                if [ "$2" != -c ] || [ "$3" != credential.helper= ] ||
                   [ "$4" != -c ] || [ "$5" != 'credential.helper=!gh auth git-credential' ]; then
                  printf '%s\n' 'Unexpected credential configuration' >&2; exit 99
                fi
                if [ "$HISTORY_SCENARIO" = transport ]; then
                  printf '%s\n' 'transport failed: token=git-secret' >&2; exit 128
                fi
                printf 'not-a-hash\trefs/heads/memory/ci-failure-analysis\n'
                ;;
              *) printf '%s\n' 'Unexpected Git mutation' >&2; exit 99 ;;
            esac
            """);
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = $"{fakeBin}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["HISTORY_SCENARIO"] = scenario,
        };
        var directory = Path.Combine(_workspace.Path, "history-checkout");
        var notes = Path.Combine(_workspace.Path, "operator-notes.txt");
        await File.WriteAllTextAsync(notes, "keep this");

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            new CauseHistoryStore(new AnalyzeCiFailure.AnalyzerCommandExecutor(_workspace.Path, environment), "microsoft/aspire", directory, new AnalysisDiagnostics()).OpenForPublicationAsync());

        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory));
        Assert.Equal("keep this", await File.ReadAllTextAsync(notes));
    }

    private async Task CreateMemoryRemoteAsync(bool seed)
    {
        var remoteDirectory = Path.Combine(_workspace.Path, "remote repository.git");
        foreach (var arguments in new string[][]
        {
            ["--no-pager", "init"],
            ["--no-pager", "init", "--bare", remoteDirectory],
            ["--no-pager", "remote", "add", "origin", new Uri(remoteDirectory).AbsoluteUri],
        })
        {
            var result = await RunProcessAsync("git", arguments);
            Assert.True(result.ExitCode == 0, result.Output);
        }
        if (!seed)
        {
            return;
        }
        foreach (var contents in new[] { "first", "second" })
        {
            await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "stored-history.json"), contents);
            var staged = await RunProcessAsync("git", ["--no-pager", "add", "--", "stored-history.json"]);
            Assert.True(staged.ExitCode == 0, staged.Output);
            var committed = await RunProcessAsync(
                "git",
                ["--no-pager", "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "Seed history fixture"]);
            Assert.True(committed.ExitCode == 0, committed.Output);
        }
        var pushed = await RunProcessAsync("git", ["--no-pager", "push", "origin", "HEAD:refs/heads/memory/ci-failure-analysis"]);
        Assert.True(pushed.ExitCode == 0, pushed.Output);
    }

    private async Task AssertPublicationGitConfigurationAsync(string directory)
    {
        foreach (var (name, expected) in new[]
        {
            ("user.name", "github-actions[bot]"),
            ("user.email", "github-actions[bot]@users.noreply.github.com"),
            ("credential.helper", "!gh auth git-credential"),
        })
        {
            var result = await RunProcessAsync("git", ["--no-pager", "-C", directory, "config", "--local", "--get", name]);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, result.Output.Trim());
        }
    }
}
