// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Cli.Git;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Git;

public class GitRepositoryTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GetIncludedFilesAsync_OutsideRepo_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext();
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(executionContext, new TestEnvironment(), NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_InGitRepo_ReturnsTrackedAndUntracked_ExcludingIgnored()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        // Tracked file under App/.
        var appDir = workspace.WorkspaceRoot.CreateSubdirectory("App");
        var trackedFile = Path.Combine(appDir.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");

        // Untracked file under another subdirectory.
        var samplesDir = workspace.WorkspaceRoot.CreateSubdirectory("samples");
        var untrackedFile = Path.Combine(samplesDir.FullName, "Sample.csproj");
        await File.WriteAllTextAsync(untrackedFile, "Not a real project file.");

        // Gitignore rule that excludes bin/.
        var gitignorePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "bin/\n");

        // Ignored file under bin/.
        var binDir = workspace.WorkspaceRoot.CreateSubdirectory("bin");
        var ignoredFile = Path.Combine(binDir.FullName, "Stale.csproj");
        await File.WriteAllTextAsync(ignoredFile, "Not a real project file.");

        // Stage and commit the tracked file so it shows up under --cached.
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "App/AppHost.csproj", ".gitignore");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        var executionContext = workspace.CreateExecutionContext();
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(executionContext, new TestEnvironment(), NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        Assert.Contains(Path.GetFullPath(trackedFile), result!);
        Assert.Contains(Path.GetFullPath(untrackedFile), result);
        Assert.DoesNotContain(Path.GetFullPath(ignoredFile), result);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_DeletedTrackedFile_StillReturned()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        // Remove the file from the working tree without telling git, so it is still
        // listed by `git ls-files --cached`.
        File.Delete(trackedFile);

        var executionContext = workspace.CreateExecutionContext();
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(executionContext, new TestEnvironment(), NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        Assert.Contains(Path.GetFullPath(trackedFile), result!);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_EmitsProfilingActivityForGitProcess()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        // ActivitySource listeners are process-wide, so this test can observe profiling spans
        // from other tests running in parallel. Use a unique session id and filter by it instead
        // of assuming every observed activity belongs to this git invocation.
        var sessionId = $"git-{Guid.NewGuid():N}";
        var startedActivities = new ConcurrentBag<Activity>();
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, sessionId));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Add);
        var executionContext = workspace.CreateExecutionContext();
        var repo = new GitRepository(executionContext, new TestEnvironment(), NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        var startedActivity = Assert.Single(startedActivities, activity =>
            IsActivityFromSession(activity, ProfilingTelemetry.Activities.Process, sessionId) &&
            activity.GetTagItem(ProfilingTelemetry.Tags.GitCommand) as string == "ls-files");
        Assert.Equal(ProfilingTelemetry.Activities.Process, startedActivity.OperationName);
        Assert.Equal("process git", startedActivity.DisplayName);
        Assert.Equal("ls-files", startedActivity.GetTagItem(ProfilingTelemetry.Tags.GitCommand));
        Assert.Equal(workspace.WorkspaceRoot.FullName, startedActivity.GetTagItem(ProfilingTelemetry.Tags.GitWorkingDirectory));
        Assert.Equal("git", startedActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutableName));
        Assert.Equal("git", startedActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath));
        Assert.Equal(new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" }, Assert.IsType<string[]>(startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
        Assert.Equal(5, startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
        Assert.Equal(0, startedActivity.GetTagItem(TelemetryConstants.Tags.ProcessExitCode));
        Assert.True((int)startedActivity.GetTagItem(TelemetryConstants.Tags.ProcessPid)! > 0);
        Assert.True((int)startedActivity.GetTagItem(ProfilingTelemetry.Tags.GitStdoutLength)! > 0);
        Assert.Equal(sessionId, startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    [Fact]
    public async Task ExplicitRootOperations_HandleTrackedUntrackedIgnoredNegatedAndUnusualPaths()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var firstOutput = workspace.WorkspaceRoot.CreateSubdirectory("output with spaces");
        var secondOutput = workspace.WorkspaceRoot.CreateSubdirectory("output-two");
        var trackedIgnored = Path.Combine(firstOutput.FullName, "tracked.ignored");
        var deletedTracked = Path.Combine(firstOutput.FullName, "deleted tracked.txt");
        var untrackedFileName = OperatingSystem.IsWindows() ? "untracked file.txt" : "line\nbreak.txt";
        var untracked = Path.Combine(secondOutput.FullName, untrackedFileName);
        var ignored = Path.Combine(secondOutput.FullName, "excluded.ignored");
        var negated = Path.Combine(secondOutput.FullName, "keep.ignored");
        await File.WriteAllTextAsync(trackedIgnored, "tracked");
        await File.WriteAllTextAsync(deletedTracked, "deleted");
        await File.WriteAllTextAsync(untracked, "untracked");
        await File.WriteAllTextAsync(ignored, "ignored");
        await File.WriteAllTextAsync(negated, "included");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore"),
            "*.ignored\n!keep.ignored\n");
        await GitTestHelper.RunGitAsync(
            workspace.WorkspaceRoot.FullName,
            "add",
            "-f",
            "output with spaces/tracked.ignored",
            "output with spaces/deleted tracked.txt",
            ".gitignore");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");
        File.Delete(deletedTracked);

        var executionContext = workspace.CreateExecutionContext();
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(
            executionContext,
            new TestEnvironment(),
            NullLogger<GitRepository>.Instance,
            profilingTelemetry);

        var root = await repo.GetRootAsync(firstOutput, TestContext.Current.CancellationToken);
        var result = await repo.GetIncludedFilesAsync(
            root!,
            [firstOutput.FullName, secondOutput.FullName],
            TestContext.Current.CancellationToken);

        Assert.Equal(workspace.WorkspaceRoot.FullName, root!.FullName);
        Assert.NotNull(result);
        Assert.Contains(Path.GetFullPath(trackedIgnored), result!);
        Assert.Contains(Path.GetFullPath(deletedTracked), result);
        Assert.Contains(Path.GetFullPath(untracked), result);
        Assert.Contains(Path.GetFullPath(negated), result);
        Assert.DoesNotContain(Path.GetFullPath(ignored), result);
    }

    [Fact]
    public async Task GetIgnoredFilesAsync_DistinguishesNotIgnoredFromGitFailure()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore"),
            "*.tmp\n!keep.tmp\n");
        var ignored = Path.Combine(workspace.WorkspaceRoot.FullName, "generated", "missing.tmp");
        var negated = Path.Combine(workspace.WorkspaceRoot.FullName, "generated", "keep.tmp");
        var ordinary = Path.Combine(workspace.WorkspaceRoot.FullName, "generated", "ordinary.txt");

        var executionContext = workspace.CreateExecutionContext();
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(
            executionContext,
            new TestEnvironment(),
            NullLogger<GitRepository>.Instance,
            profilingTelemetry);

        var result = await repo.GetIgnoredFilesAsync(
            workspace.WorkspaceRoot,
            [ignored, negated, ordinary],
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal([Path.GetFullPath(ignored)], result);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_PathOutsideExplicitRoot_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outside = Directory.CreateTempSubdirectory("aspire-git-outside-");
        try
        {
            var executionContext = workspace.CreateExecutionContext();
            using var profilingTelemetry = CreateProfilingTelemetry();
            var repo = new GitRepository(
                executionContext,
                new TestEnvironment(),
                NullLogger<GitRepository>.Instance,
                profilingTelemetry);

            await Assert.ThrowsAsync<ArgumentException>(
                () => repo.GetIncludedFilesAsync(
                    workspace.WorkspaceRoot,
                    [outside.FullName],
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    private static bool IsActivityFromSession(Activity activity, string operationName, string sessionId)
    {
        return activity.OperationName == operationName &&
            Equals(sessionId, activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    private static ProfilingTelemetry CreateProfilingTelemetry(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return new ProfilingTelemetry(configuration);
    }

}
