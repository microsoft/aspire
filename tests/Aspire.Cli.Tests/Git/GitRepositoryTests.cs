// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Cli.Git;
using Aspire.Cli.Mcp;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Cli.Tests.Git;

public class GitRepositoryTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetIncludedFilesAsync_LogsGitOutput(bool initialized)
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (initialized)
        {
            await workspace.InitializeGitAsync();
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "example.txt"), "example");
        }
        using var profilingTelemetry = CreateProfilingTelemetry();
        var logger = new FakeLogger<GitRepository>();
        var repo = new GitRepository(workspace.CreateExecutionContext(), new TestEnvironment(), logger, profilingTelemetry);

        var result = await repo.GetIncludedFilesAsync(workspace.WorkspaceRoot, TestContext.Current.CancellationToken).DefaultTimeout();

        var stdout = Assert.Single(logger.Collector.GetSnapshot(), entry => entry.Message.StartsWith("Git stdout: ", StringComparison.Ordinal));
        var stderr = Assert.Single(logger.Collector.GetSnapshot(), entry => entry.Message.StartsWith("Git stderr: ", StringComparison.Ordinal));
        if (initialized)
        {
            Assert.Equal([Path.Combine(workspace.Path, "example.txt")], Assert.IsAssignableFrom<IReadOnlySet<string>>(result));
            Assert.Equal("Git stdout: example.txt\0", stdout.Message);
            Assert.Equal("Git stderr: ", stderr.Message);
        }
        else
        {
            Assert.Null(result);
            Assert.Equal("Git stdout: ", stdout.Message);
            Assert.StartsWith("Git stderr: fatal: not a git repository", stderr.Message, StringComparison.Ordinal);
            Assert.Equal(stderr.Message.TrimEnd(), stderr.Message);
        }
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.Windows)]
    public async Task GetRootAsync_WhileMcpReadsStandardInput_Completes()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await workspace.InitializeGitAsync();
        var options = new RemoteInvokeOptions
        {
            StartInfo = { RedirectStandardInput = true, RedirectStandardOutput = true }
        };
        options.StartInfo.Environment.Remove("GIT_REDIRECT_STDIN");

        // A real stdio server reproduces the inherited pipe read without changing the test host's stdin.
        using var remote = RemoteExecutor.Invoke(static async directoryPath =>
        {
            var directory = new DirectoryInfo(directoryPath);
            var context = new CliExecutionContext(directory, directory, directory, directory, directory, "unused.log", "local");
            using var profilingTelemetry = CreateProfilingTelemetry();
            var repository = new GitRepository(context, new TestEnvironment(), NullLogger<GitRepository>.Instance, profilingTelemetry);
            var serverOptions = new McpServerOptions
            {
                ServerInfo = new() { Name = "git-test", Version = "1.0" },
                Handlers = new()
                {
                    CallToolHandler = async (_, cancellationToken) =>
                    {
                        var root = await repository.GetRootAsync(cancellationToken);
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = root?.FullName ?? string.Empty }]
                        };
                    }
                }
            };
            var transport = new StdioMcpTransportFactory(NullLoggerFactory.Instance).CreateTransport();
            await using var server = McpServer.Create(transport, serverOptions);
            await server.RunAsync();
        }, workspace.WorkspaceRoot.FullName, options);

        try
        {
            var clientTransport = new StreamClientTransport(
                serverInput: remote.Process.StandardInput.BaseStream,
                serverOutput: remote.Process.StandardOutput.BaseStream);
            await using var client = await McpClient.CreateAsync(clientTransport).DefaultTimeout();
            using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutDuration);
            var result = await client.CallToolAsync("git_root", cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            Assert.Collection(result.Content,
                block => Assert.Equal(workspace.WorkspaceRoot.FullName, Assert.IsType<TextContentBlock>(block).Text));
        }
        finally
        {
            remote.Process.StandardInput.Close();
        }
    }

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
