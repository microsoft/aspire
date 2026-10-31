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
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Git;

public class GitRepositoryTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GetIncludedFilesAsync_OutsideRepo_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var profilingTelemetry = CreateProfilingTelemetry();
        using var loggerFactory = CreateLoggerFactory();
        var repo = CreateGitRepository(workspace, loggerFactory, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_InGitRepo_ReturnsTrackedAndUntracked_ExcludingIgnored()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName, outputHelper);

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
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "add", "App/AppHost.csproj", ".gitignore");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "commit", "-m", "init");

        using var profilingTelemetry = CreateProfilingTelemetry();
        using var loggerFactory = CreateLoggerFactory();
        var repo = CreateGitRepository(workspace, loggerFactory, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        Assert.Contains(Path.GetFullPath(trackedFile), result!);
        Assert.Contains(Path.GetFullPath(untrackedFile), result);
        Assert.DoesNotContain(Path.GetFullPath(ignoredFile), result);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_DeletedTrackedFile_StillReturned()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName, outputHelper);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "commit", "-m", "init");

        // Remove the file from the working tree without telling git, so it is still
        // listed by `git ls-files --cached`.
        File.Delete(trackedFile);

        using var profilingTelemetry = CreateProfilingTelemetry();
        using var loggerFactory = CreateLoggerFactory();
        var repo = CreateGitRepository(workspace, loggerFactory, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        Assert.Contains(Path.GetFullPath(trackedFile), result!);
    }

    [Fact]
    public async Task GetIncludedFilesAsync_EmitsProfilingActivityForGitProcess()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName, outputHelper);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "commit", "-m", "init");

        // ActivitySource listeners are process-wide, so this test can observe profiling spans
        // from other tests running in parallel. Use a unique session id and filter by it instead
        // of assuming every observed activity belongs to this git invocation.
        var sessionId = $"git-{Guid.NewGuid():N}";
        var startedActivities = new ConcurrentBag<Activity>();
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, sessionId));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Add);
        using var loggerFactory = CreateLoggerFactory();
        var repo = CreateGitRepository(workspace, loggerFactory, profilingTelemetry);

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
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a POSIX shell fsmonitor hook to leave a descendant holding git's stderr pipe.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task GetIncludedFilesAsync_DescendantHoldsInheritedPipe_ReturnsAfterGitExits()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName, outputHelper);

        var trackedFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(trackedFile, "Not a real project file.");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "add", "AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "commit", "-m", "init");

        // git runs the core.fsmonitor hook as a child process. The hook leaves a background
        // process that inherits git's stderr pipe and outlives git, so stderr never reaches EOF
        // after git exits. This is the same shape as any descendant (or a concurrently spawned
        // process that inherited the pipe handle) keeping git's output pipes open.
        var hooksDir = Directory.CreateTempSubdirectory("aspire-git-hook-");
        var pidFile = Path.Combine(hooksDir.FullName, "pids");
        var hookPath = Path.Combine(hooksDir.FullName, "fsmonitor-hook.sh");
        await File.WriteAllTextAsync(hookPath, $"""
            #!/bin/sh
            sleep 300 >/dev/null &
            echo $! >> '{pidFile}'
            exit 1

            """);
        File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, outputHelper, "config", "core.fsmonitor", hookPath);

        try
        {
            using var profilingTelemetry = CreateProfilingTelemetry();
            using var loggerFactory = CreateLoggerFactory();
            var repo = CreateGitRepository(workspace, loggerFactory, profilingTelemetry);

            var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

            Assert.True(File.Exists(pidFile), "The fsmonitor hook was not invoked by git ls-files.");
            Assert.NotNull(result);
            Assert.Contains(Path.GetFullPath(trackedFile), result!);
        }
        finally
        {
            KillProcessesFromPidFile(pidFile);
            hooksDir.Delete(recursive: true);
        }
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a POSIX shell script as a fake git executable.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task GetIncludedFilesAsync_DescendantHoldsStdoutAfterGitExits_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // A fake git that writes part of its output, then exits while a background process
        // still holds its stdout pipe. stdout never reaches EOF, so the output can't be trusted
        // to be complete and GetIncludedFilesAsync must return null (fall back to the
        // filesystem walker) instead of hanging or returning a partial file list.
        var fakeGitDir = Directory.CreateTempSubdirectory("aspire-fake-git-");
        var pidFile = Path.Combine(fakeGitDir.FullName, "pids");
        var fakeGitPath = Path.Combine(fakeGitDir.FullName, "git");
        await File.WriteAllTextAsync(fakeGitPath, $"""
            #!/bin/sh
            printf 'AppHost.csproj\0'
            sleep 300 2>/dev/null &
            echo $! >> '{pidFile}'
            exit 0

            """);
        File.SetUnixFileMode(fakeGitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var profilingTelemetry = CreateProfilingTelemetry();
            using var loggerFactory = CreateLoggerFactory();
            var repo = new GitRepository(workspace.CreateExecutionContext(), new TestEnvironment(), loggerFactory.CreateLogger<GitRepository>(), profilingTelemetry, fakeGitPath);

            var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

            Assert.True(File.Exists(pidFile), "The fake git did not start a background process.");
            Assert.Null(result);
        }
        finally
        {
            KillProcessesFromPidFile(pidFile);
            fakeGitDir.Delete(recursive: true);
        }
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a POSIX shell script as a fake git executable.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task GetIncludedFilesAsync_FirstPathStartsWithByteOrderMark_PreservesPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // U+FEFF is a valid file name character. When it starts the first path in the output it
        // must not be treated as a byte order mark and stripped.
        var fakeGitDir = Directory.CreateTempSubdirectory("aspire-fake-git-");
        var fakeGitPath = Path.Combine(fakeGitDir.FullName, "git");
        await File.WriteAllTextAsync(fakeGitPath, """
            #!/bin/sh
            printf '\357\273\277AppHost.csproj\000Other.csproj\000'
            exit 0

            """);
        File.SetUnixFileMode(fakeGitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var profilingTelemetry = CreateProfilingTelemetry();
            using var loggerFactory = CreateLoggerFactory();
            var repo = new GitRepository(workspace.CreateExecutionContext(), new TestEnvironment(), loggerFactory.CreateLogger<GitRepository>(), profilingTelemetry, fakeGitPath);

            var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

            Assert.NotNull(result);
            Assert.Equal(
                new[]
                {
                    Path.Combine(workspace.WorkspaceRoot.FullName, "\uFEFFAppHost.csproj"),
                    Path.Combine(workspace.WorkspaceRoot.FullName, "Other.csproj")
                }.Order(StringComparer.Ordinal),
                result!.Order(StringComparer.Ordinal));
        }
        finally
        {
            fakeGitDir.Delete(recursive: true);
        }
    }

    private static void KillProcessesFromPidFile(string pidFile)
    {
        if (!File.Exists(pidFile))
        {
            return;
        }

        foreach (var line in File.ReadAllLines(pidFile))
        {
            if (int.TryParse(line, out var pid))
            {
                try
                {
                    using var orphan = Process.GetProcessById(pid);
                    orphan.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private static GitRepository CreateGitRepository(TemporaryWorkspace workspace, ILoggerFactory loggerFactory, ProfilingTelemetry profilingTelemetry)
    {
        return new GitRepository(workspace.CreateExecutionContext(), new TestEnvironment(), loggerFactory.CreateLogger<GitRepository>(), profilingTelemetry);
    }

    private ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddXunit(outputHelper));

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
