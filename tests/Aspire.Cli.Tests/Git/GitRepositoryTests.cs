// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Cli.Git;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Git;

public class GitRepositoryTests(ITestOutputHelper outputHelper)
{
    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        var settings = workingDirectory.CreateSubdirectory(".aspire-cli-state");
        var hives = settings.CreateSubdirectory("hives");
        var cache = settings.CreateSubdirectory("cache");
        var sdks = settings.CreateSubdirectory("sdks");
        var logs = settings.CreateSubdirectory("logs");
        return new CliExecutionContext(workingDirectory, hives, cache, sdks, logs, "test.log");
    }

    [Fact]
    public async Task GetIncludedFilesAsync_OutsideRepo_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(executionContext, NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_InGitRepo_ReturnsTrackedAndUntracked_ExcludingIgnored()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(executionContext, NullLogger<GitRepository>.Instance, profilingTelemetry);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        // Remove the file from the working tree without telling git, so it is still
        // listed by `git ls-files --cached`.
        File.Delete(trackedFile);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        using var profilingTelemetry = CreateProfilingTelemetry();
        var repo = new GitRepository(executionContext, NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        Assert.Contains(Path.GetFullPath(trackedFile), result!);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_EmitsProfilingActivityForGitProcess()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        var startedActivities = new ConcurrentBag<Activity>();
        using var listener = CreateProfilingActivityListener(startedActivities.Add);
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var repo = new GitRepository(executionContext, NullLogger<GitRepository>.Instance, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        var startedActivity = Assert.Single(startedActivities, activity =>
            activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId) as string == "session-1" &&
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
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    private static ProfilingTelemetry CreateProfilingTelemetry(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return new ProfilingTelemetry(configuration);
    }

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

}
