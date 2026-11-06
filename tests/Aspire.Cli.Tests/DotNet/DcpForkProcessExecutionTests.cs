// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Dcp;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

public sealed class DcpForkProcessExecutionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("123", "10.0.403", 0, true)]
    [InlineData("123", null, 0, false)]
    [InlineData(null, null, 0, false)]
    [InlineData(" 123", "10.0.403", 0, false)]
    [InlineData("-1", "10.0.403", 0, false)]
    [InlineData("0", "10.0.403", 0, false)]
    [InlineData("2147483648", "10.0.403", 0, false)]
    [InlineData("123", "11.0.100", 0, false)]
    [InlineData("123", "10.0.403", 1, false)]
    public async Task ProbeRequiresPidAndActualSdkOutputBeforeLaunchingWatcher(
        string? pid, string? sdk, int exitCode, bool supported)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var outputLines = new List<string>();
        var errors = new List<string>();
        var factory = new TestProcessExecutionFactory
        {
            AttemptCallback = (attempt, options) =>
            {
                if (attempt == 1)
                {
                    if (pid is not null)
                    {
                        options.StandardOutputCallback!(pid);
                    }
                    if (sdk is not null)
                    {
                        options.StandardOutputCallback!(sdk);
                    }
                    return (exitCode, null);
                }
                options.StandardOutputCallback!("456");
                options.StandardOutputCallback!("789");
                options.StandardOutputCallback!("");
                options.StandardErrorCallback!("application error");
                return (7, null);
            }
        };
        await using var execution = CreateExecution(workspace, factory, new()
        {
            StandardOutputCallback = outputLines.Add,
            StandardErrorCallback = errors.Add
        });

        if (!supported)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => execution.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(RunCommandStrings.WatchDcpUnavailable, exception.Message);
            Assert.Equal(1, factory.AttemptCount);
        }
        else
        {
            Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(456, execution.ProcessId);
            Assert.Equal(7, await execution.WaitForExitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(["789", ""], outputLines);
            Assert.Equal(["application error"], errors);
            Assert.Equal(2, factory.AttemptCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeRejectsOversizedOrUnexpectedOutput(bool stderr)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var factory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, options) =>
            {
                options.StandardOutputCallback!("123");
                options.StandardOutputCallback!("10.0.403");
                (stderr ? options.StandardErrorCallback : options.StandardOutputCallback)!(new string('a', 4097));
                return (0, null);
            }
        };
        await using var execution = CreateExecution(workspace, factory, new());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RunCommandStrings.WatchDcpUnavailable, exception.Message);
        Assert.Equal(1, factory.AttemptCount);
    }

    [Fact]
    public async Task ForkPreservesResolvedCommandEnvironmentAndUsesLongLivedCliIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var captures = new List<(ProcessStartInfo StartInfo, ProcessInvocationOptions Options)>();
        var factory = new TestProcessExecutionFactory();
        factory.CreateExecutionFromStartInfoCallback = (startInfo, options) =>
        {
            captures.Add((startInfo, options));
            return new TestProcessExecution(startInfo.FileName, startInfo.ArgumentList.ToArray(), null, options,
                (_, settings, _) =>
                {
                    settings.StandardOutputCallback!("123");
                    if (captures.Count == 1)
                    {
                        settings.StandardOutputCallback!("10.0.403");
                    }
                    return Task.FromResult((0, (string?)null));
                }, () => captures.Count);
        };
        var command = new ProcessStartInfo("/selected/sdk/dotnet",
            ["exec", "watch.dll", "host", "--launch-profile=https", "--", "--", "--password", "secret", ""])
        {
            WorkingDirectory = workspace.WorkspaceRoot.FullName
        };
        command.Environment.Clear();
        command.Environment["DOTNET_ROOT"] = "/selected/sdk";
        command.Environment["MSBuildSDKsPath"] = "/selected/sdk/10.0.403/Sdks";
        command.Environment["ASPIRE_DOTNET_WATCH_HOST_SESSION"] = "this-generation";
        await using var execution = new DcpForkProcessExecution(command, new("/selected/dcp", "10.0.403"),
            new() { KillOnParentExit = true, Lifetime = ChildProcessLifetime.OwnedTree, AppHostArgumentStartIndex = 6 },
            factory, NullLogger.Instance, isWindows: false, DcpForkChildProcess.Open);

        Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await execution.WaitForExitAsync(TestContext.Current.CancellationToken));

        using var owner = Process.GetCurrentProcess();
        var identity = DcpProcessMonitor.GetMonitorProcessIdentity(owner);
        Assert.Equal(2, captures.Count);
        foreach (var (startInfo, options) in captures)
        {
            Assert.Equal("/selected/dcp", startInfo.FileName);
            Assert.Equal(workspace.WorkspaceRoot.FullName, startInfo.WorkingDirectory);
            Assert.Equal(command.Environment, startInfo.Environment);
            Assert.Equal(["fork-process", "--monitor", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--monitor-identity-time", identity.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                "--enable-child-cleanup", "--skip-descendants", "--", command.FileName], startInfo.ArgumentList.Take(9));
            Assert.Equal(ChildProcessLifetime.AppHost, options.Lifetime);
            Assert.False(options.KillOnParentExit);
            Assert.False(options.KillEntireProcessTreeOnCancel);
            Assert.True(options.IsolateConsole);
            Assert.True(options.SuppressLogging);
            Assert.Null(options.DcpFork);
        }
        Assert.Equal(["--version"], captures[0].StartInfo.ArgumentList.Skip(9));
        Assert.Equal(command.ArgumentList, captures[1].StartInfo.ArgumentList.Skip(9));
        Assert.Equal(command.ArgumentList, execution.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task MissingOrInvalidWatcherPidIsFailureNotOrdinaryFallback(string? pid)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var factory = new TestProcessExecutionFactory
        {
            AttemptCallback = (attempt, options) =>
            {
                if (attempt == 1)
                {
                    options.StandardOutputCallback!("123");
                    options.StandardOutputCallback!("10.0.403");
                }
                else if (pid is not null)
                {
                    options.StandardOutputCallback!(pid);
                }
                return (0, null);
            }
        };
        await using var execution = CreateExecution(workspace, factory, new());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RunCommandStrings.WatchDcpUnavailable, exception.Message);
        Assert.Equal(2, factory.AttemptCount);
    }

    [Theory]
    [InlineData("mismatched-parent", false)]
    [InlineData("transport-exited", false)]
    [InlineData("identity-unreadable", false)]
    [InlineData("api-unavailable", false)]
    [InlineData("root-exited", true)]
    [InlineData("root-exited-during-query", true)]
    [InlineData("retained-root", true)]
    [InlineData("cleanup-failed", true)]
    public async Task WindowsHandoffRetainsOnlyTheOriginalOwnedSdkHandle(string scenario, bool accepted)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var exited = new TaskCompletionSource<(int, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportAlive = true;
        var childAlive = true;
        var opened = 0;
        var stops = new List<string[]>();
        var child = new TestDcpForkChildProcess
        {
            ReadHasExited = () => !childAlive,
            ReadIdentity = () =>
            {
                if (scenario is "transport-exited" or "identity-unreadable")
                {
                    transportAlive = false;
                    exited.TrySetResult((130, null));
                }
                if (scenario == "identity-unreadable")
                {
                    throw new InvalidOperationException("Identity unavailable for a live SDK root.");
                }
                return new(456, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
            },
            ReadParentProcessId = () =>
            {
                if (scenario is "mismatched-parent" or "api-unavailable")
                {
                    transportAlive = false;
                    exited.TrySetResult((130, null));
                }
                if (scenario == "api-unavailable")
                {
                    throw new EntryPointNotFoundException("NtQueryInformationProcess");
                }
                if (scenario == "root-exited-during-query")
                {
                    childAlive = false;
                    transportAlive = false;
                    exited.TrySetResult((7, null));
                    throw new InvalidOperationException("SDK root exited during parent query.");
                }
                return scenario == "mismatched-parent" ? 999 : 123;
            }
        };
        var factory = new TestProcessExecutionFactory
        {
            CreateExecutionFromStartInfoCallback = (startInfo, options) =>
            {
                var probe = startInfo.ArgumentList.Last() == "--version";
                return new TestProcessExecution(startInfo.FileName, startInfo.ArgumentList.ToArray(), null, options,
                    (_, settings, _) =>
                    {
                        settings.StandardOutputCallback!(probe ? "123" : "456");
                        if (probe)
                        {
                            settings.StandardOutputCallback!("10.0.403");
                            return Task.FromResult((0, (string?)null));
                        }
                        return exited.Task;
                    }, () => 1)
                {
                    ProcessId = 123,
                    HasExitedCallback = () => probe || !transportAlive
                };
            },
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, directory, settings) =>
            {
                stops.Add(arguments);
                childAlive = false;
                transportAlive = false;
                child.ExitCode = 130;
                exited.TrySetResult((scenario == "cleanup-failed" ? 1 : 130, null));
                return new TestProcessExecution(fileName, arguments, environment, settings,
                    (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
            }
        };
        var execution = new DcpForkProcessExecution(
            new ProcessStartInfo("/selected/dotnet", ["exec", "watch.dll", "host"]) { WorkingDirectory = workspace.Path },
            new("/selected/dcp", "10.0.403"), new(), factory, NullLogger.Instance, isWindows: true, pid =>
            {
                opened++;
                Assert.Equal(456, pid);
                if (scenario == "root-exited")
                {
                    childAlive = false;
                    transportAlive = false;
                    exited.TrySetResult((7, null));
                }
                return child;
            });
        try
        {
            if (!accepted)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => execution.StartAsync(TestContext.Current.CancellationToken));
                Assert.Equal(RunCommandStrings.WatchDcpUnavailable, failure.Message);
                Assert.Empty(stops);
            }
            else
            {
                Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
                if (scenario is "retained-root" or "cleanup-failed")
                {
                    using var cancellation = new CancellationTokenSource();
                    var wait = execution.WaitForExitAsync(cancellation.Token);
                    await cancellation.CancelAsync();
                    var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
                    Assert.Equal(cancellation.Token, failure.CancellationToken);
                    Assert.Equal(scenario == "cleanup-failed", DcpForkShutdownException.ContainsFailure(failure));
                    Assert.Equal(["stop-process-tree", "--skip-descendants", "--pid", "456",
                        "--process-start-time", "2026-11-06T12:00:00.000Z"], Assert.Single(stops));
                    Assert.Equal(1, child.IdentityReads);
                }
                else
                {
                    Assert.Equal(7, await execution.WaitForExitAsync(TestContext.Current.CancellationToken));
                    Assert.Empty(stops);
                }
            }
            Assert.Equal(1, opened);
        }
        finally
        {
            transportAlive = false;
            childAlive = false;
            exited.TrySetResult((130, null));
            await execution.DisposeAsync();
        }
        Assert.Equal(1, child.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task PrototypePreservesApplicationOutputAndActualExitCode(int exitCode)
    {
        var path = GetPrototypePath();
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        var fixture = await ProcessTestHelpers.CreateForkApplicationAsync(workspace, factory, TestContext.Current.CancellationToken);
        var stdout = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var execution = factory.CreateExecution(fixture.DotnetPath,
            ["exec", fixture.ApplicationPath, exitCode.ToString(CultureInfo.InvariantCulture)],
            env: null, workspace.WorkspaceRoot, new()
            {
                DcpFork = new(path, fixture.SdkVersion),
                StandardOutputCallback = stdout.Enqueue,
                StandardErrorCallback = stderr.Enqueue
            });

        Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(exitCode, await execution.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["application stdout"], stdout);
        Assert.Equal(["application stderr"], stderr.Where(line => line == "application stderr"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrototypeCompletionAndDisposalAreBoundedDespiteChattyDescendant(bool cancel)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This descendant fixture requires Unix sessions and /bin/sh.");
        var path = GetPrototypePath();
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        var fixture = await ProcessTestHelpers.CreateForkApplicationAsync(workspace, factory, TestContext.Current.CancellationToken);
        var pidPath = Path.Combine(workspace.Path, "writer.pid");
        var execution = factory.CreateExecution(fixture.DotnetPath,
            ["exec", fixture.ApplicationPath, cancel ? "chatty-cancel" : "chatty", pidPath],
            env: null, workspace.WorkspaceRoot, new() { DcpFork = new(path, fixture.SdkVersion) });
        var writerPid = 0;
        DateTimeOffset? writerStart = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            writerPid = await ProcessTestHelpers.WaitForProcessIdAsync(pidPath, ready.Token);
            writerStart = ProcessStartTimeHelper.TryGetProcessStartTime(writerPid);
            Assert.NotNull(writerStart);
            var completion = execution.WaitForExitAsync(cancellation.Token);
            if (cancel)
            {
                await cancellation.CancelAsync();
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => completion.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                Assert.Equal(0, await completion.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            await execution.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ProcessTestHelpers.IsProcessExited(writerPid));
        }
        finally
        {
            // Recover the writer identity even if startup/readiness failed before assignment.
            if (writerPid == 0 && File.Exists(pidPath))
            {
                writerPid = await ProcessTestHelpers.WaitForProcessIdAsync(pidPath, TestContext.Current.CancellationToken);
                writerStart = ProcessStartTimeHelper.TryGetProcessStartTime(writerPid);
            }
            if (writerPid > 0 && writerStart is not null)
            {
                ProcessSignaler.ForceKill(writerPid, writerStart, NullLogger.Instance);
                Assert.True(ProcessTestHelpers.WaitForProcessExit(writerPid, TimeSpan.FromSeconds(10)));
            }
            await execution.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("ASPIRE_TEST_DCP_PID_ONLY_PATH")]
    [InlineData("ASPIRE_TEST_DCP_RELEASED_PATH")]
    public async Task ActualOlderDcpIsRejectedBeforeWatcherLaunch(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        Assert.SkipUnless(path is { Length: > 0 }, $"Requires an explicit DCP path in {variable}.");
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        var fixture = await ProcessTestHelpers.CreateForkApplicationAsync(workspace, factory, TestContext.Current.CancellationToken);
        var pidFile = Path.Combine(workspace.Path, "not-launched.pid");
        await using var execution = factory.CreateExecution(fixture.DotnetPath,
            ["exec", fixture.ApplicationPath, "pause", pidFile],
            env: null, workspace.WorkspaceRoot, new() { DcpFork = new(path, fixture.SdkVersion) });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execution.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RunCommandStrings.WatchDcpUnavailable, failure.Message);
        Assert.False(File.Exists(pidFile));
    }

    private static string GetPrototypePath()
    {
        var path = Environment.GetEnvironmentVariable("ASPIRE_TEST_DCP_FORK_PATH");
        Assert.SkipUnless(path is { Length: > 0 }, "Requires an explicit output-preserving DCP prototype.");
        return path;
    }

    private static DcpForkProcessExecution CreateExecution(
        TemporaryWorkspace workspace, IProcessExecutionFactory factory, ProcessInvocationOptions options) =>
        new(new ProcessStartInfo("/selected/dotnet", ["exec", "watch.dll", "host"])
        {
            WorkingDirectory = workspace.WorkspaceRoot.FullName
        }, new("/selected/dcp", "10.0.403"), options, factory, NullLogger.Instance, isWindows: false, DcpForkChildProcess.Open);
}
