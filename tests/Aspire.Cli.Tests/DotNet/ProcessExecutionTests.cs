// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Cli.Tests.TestServices.ProcessTestHelpers;

namespace Aspire.Cli.Tests.DotNet;

public sealed class ProcessExecutionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WaitForExitAsync_AllowsForwardersToDrainBeforeClosingStreams()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var outputFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "output.json"));
        await File.WriteAllTextAsync(outputFile.FullName, CreateJsonPayload(lineCount: 400));

        var scriptFile = await CreateOutputScriptAsync(workspace.WorkspaceRoot, outputFile);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var firstLineSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var releaseTask = Task.Run(async () =>
        {
            await firstLineSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            releaseCallback.SetResult();
        });

        var isFirstLine = true;
        await using var execution = CreateExecution(
            scriptFile,
            new ProcessInvocationOptions
            {
                StandardOutputCallback = line =>
                {
                    if (isFirstLine)
                    {
                        isFirstLine = false;
                        firstLineSeen.TrySetResult();

                        if (!releaseCallback.Task.Wait(TimeSpan.FromSeconds(20)))
                        {
                            throw new TimeoutException("Timed out waiting to release the blocked stdout callback.");
                        }
                    }

                    stdoutBuilder.AppendLine(line);
                },
                StandardErrorCallback = line => stderrBuilder.AppendLine(line)
            });

        Assert.True(execution.Start());

        var exitCode = await execution.WaitForExitAsync(CancellationToken.None).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await releaseTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderrBuilder.ToString()));

        using var jsonDocument = JsonDocument.Parse(stdoutBuilder.ToString());
        var values = jsonDocument.RootElement.GetProperty("values");
        Assert.Equal(400, values.GetArrayLength());
        Assert.Equal("value-399", values[399].GetString());
    }

    [Fact]
    public async Task WaitForExitAsync_AllowsBufferedTailOutputAfterLongIdlePeriod()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var outputFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "output.json"));
        await File.WriteAllTextAsync(outputFile.FullName, CreateJsonPayload(lineCount: 400));

        var scriptFile = await CreateDelayedOutputScriptAsync(workspace.WorkspaceRoot, outputFile);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var firstLineSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var releaseTask = Task.Run(async () =>
        {
            await firstLineSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await Task.Delay(TimeSpan.FromMilliseconds(6500));
            releaseCallback.SetResult();
        });

        await using var execution = CreateExecution(
            scriptFile,
            new ProcessInvocationOptions
            {
                StandardOutputCallback = line =>
                {
                    stdoutBuilder.AppendLine(line);

                    if (line == "ready")
                    {
                        firstLineSeen.TrySetResult();

                        if (!releaseCallback.Task.Wait(TimeSpan.FromSeconds(20)))
                        {
                            throw new TimeoutException("Timed out waiting to release the blocked stdout callback.");
                        }
                    }
                },
                StandardErrorCallback = line => stderrBuilder.AppendLine(line)
            });

        Assert.True(execution.Start());

        var exitCode = await execution.WaitForExitAsync(CancellationToken.None).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await releaseTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderrBuilder.ToString()));

        var stdout = stdoutBuilder.ToString();
        var jsonStart = stdout.IndexOf('{', StringComparison.Ordinal);
        Assert.True(jsonStart >= 0, stdout);

        using var jsonDocument = JsonDocument.Parse(stdout[jsonStart..]);
        var values = jsonDocument.RootElement.GetProperty("values");
        Assert.Equal(400, values.GetArrayLength());
        Assert.Equal("value-399", values[399].GetString());
    }

    [Fact]
    public async Task WaitForExitAsync_KillsProcessWhenCanceled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var scriptFile = await CreateLongRunningScriptAsync(workspace.WorkspaceRoot);

        await using var execution = CreateExecution(
            scriptFile,
            new ProcessInvocationOptions());

        Assert.True(execution.Start());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitForExitAsync(cts.Token));

        Assert.True(WaitForProcessExit(execution.ProcessId, TimeSpan.FromSeconds(10)), $"Expected process {execution.ProcessId} to exit after cancellation.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForExitAsync_WithGracefulServices_InvokesSignalerAndThrowsOnCancellation(bool isolateConsole)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scriptFile = await CreateLongRunningScriptAsync(workspace.WorkspaceRoot);
        using var shutdownService = new TestGracefulShutdownWindow();
        // Model the run path: graceful shutdown is enabled (positive budget) so the coordinator runs
        // the ladder. Escalation in this test is driven explicitly (signaler kill), not by the budget.
        var signaler = new RecordingGracefulSignaler(onSignal: pid =>
        {
            TryKillProcess(pid);
            return Task.FromResult(true);
        });

        await using var execution = CreateExecution(scriptFile, isolateConsole, signaler, shutdownService);

        Assert.True(execution.Start());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitForExitAsync(cts.Token));

        Assert.Single(signaler.Pids);
        Assert.False(shutdownService.GracefulShutdownToken.IsCancellationRequested);
        Assert.True(WaitForProcessExit(execution.ProcessId, TimeSpan.FromSeconds(10)), $"Expected process {execution.ProcessId} to exit after graceful signal.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForExitAsync_WithGracefulServices_ProcessIgnoresSignal_ExpireEscalatesToKill(bool isolateConsole)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scriptFile = await CreateLongRunningScriptAsync(workspace.WorkspaceRoot);
        using var shutdownService = new TestGracefulShutdownWindow();
        // Model the run path: graceful shutdown is enabled so the coordinator runs the ladder.
        // Escalation is driven by the explicit Expire() below, not by the budget elapsing.
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaler = new RecordingGracefulSignaler(onSignal: _ =>
        {
            signaled.TrySetResult();
            return Task.FromResult(true);
        });

        await using var execution = CreateExecution(scriptFile, isolateConsole, signaler, shutdownService);

        Assert.True(execution.Start());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var waitTask = Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitForExitAsync(cts.Token));
        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        shutdownService.Expire();

        await waitTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(signaler.Pids);
        Assert.True(WaitForProcessExit(execution.ProcessId, TimeSpan.FromSeconds(10)), $"Expected process {execution.ProcessId} to be killed after graceful expiration.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForExitAsync_WithGracefulServices_SignalerThrows_StillEscalatesToKill(bool isolateConsole)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scriptFile = await CreateLongRunningScriptAsync(workspace.WorkspaceRoot);
        using var shutdownService = new TestGracefulShutdownWindow();
        // Model the run path: graceful shutdown is enabled so the coordinator runs the ladder.
        var signaler = new RecordingGracefulSignaler(onSignal: _ =>
            throw new InvalidOperationException("simulated DCP failure"));

        await using var execution = CreateExecution(scriptFile, isolateConsole, signaler, shutdownService);

        Assert.True(execution.Start());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        shutdownService.Expire();

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitForExitAsync(cts.Token));

        Assert.Single(signaler.Pids);
        Assert.True(WaitForProcessExit(execution.ProcessId, TimeSpan.FromSeconds(10)), $"Expected process {execution.ProcessId} to be killed after signaler failure.");
    }

    private static string CreateJsonPayload(int lineCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"values\": [");

        for (var i = 0; i < lineCount; i++)
        {
            var suffix = i == lineCount - 1 ? string.Empty : ",";
            builder.AppendLine($"    \"value-{i}\"{suffix}");
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static async Task<FileInfo> CreateOutputScriptAsync(DirectoryInfo workspaceRoot, FileInfo outputFile)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "emit-output.cmd"));
            var content =
                "@echo off" + Environment.NewLine +
                $"type \"{outputFile.FullName}\"" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);
            return scriptFile;
        }
        else
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "emit-output.sh"));
            var content =
                "#!/usr/bin/env bash" + Environment.NewLine +
                $"cat \"{outputFile.FullName}\"" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);

            File.SetUnixFileMode(
                scriptFile.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return scriptFile;
        }
    }

    private static async Task<FileInfo> CreateDelayedOutputScriptAsync(DirectoryInfo workspaceRoot, FileInfo outputFile)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "emit-delayed-output.cmd"));
            var content =
                "@echo off" + Environment.NewLine +
                "echo ready" + Environment.NewLine +
                // Use ping instead of powershell to avoid variable PowerShell
                // cold-start overhead on loaded CI agents (can add 10-20s).
                "ping -n 7 127.0.0.1 > nul" + Environment.NewLine +
                $"type \"{outputFile.FullName}\"" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);
            return scriptFile;
        }
        else
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "emit-delayed-output.sh"));
            var content =
                "#!/usr/bin/env bash" + Environment.NewLine +
                "echo ready" + Environment.NewLine +
                "sleep 6" + Environment.NewLine +
                $"cat \"{outputFile.FullName}\"" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);

            File.SetUnixFileMode(
                scriptFile.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return scriptFile;
        }
    }

    private static async Task<FileInfo> CreateLongRunningScriptAsync(DirectoryInfo workspaceRoot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "long-running.cmd"));
            var content =
                "@echo off" + Environment.NewLine +
                "powershell -NoProfile -Command \"Start-Sleep -Seconds 60\"" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);
            return scriptFile;
        }
        else
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "long-running.sh"));
            var content =
                "#!/usr/bin/env bash" + Environment.NewLine +
                "sleep 60" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);

            File.SetUnixFileMode(
                scriptFile.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return scriptFile;
        }
    }

    private static ProcessStartInfo CreateStartInfo(FileInfo scriptFile)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = scriptFile.Directory!.FullName,
                ArgumentList = { "/d", "/c", scriptFile.FullName }
            };
        }

        return new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = scriptFile.Directory!.FullName,
            ArgumentList = { scriptFile.FullName }
        };
    }

    private static IProcessExecution CreateExecution(
        FileInfo scriptFile,
        ProcessInvocationOptions options)
    {
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        var startInfo = CreateStartInfo(scriptFile);

        return factory.CreateExecution(
            startInfo.FileName,
            startInfo.ArgumentList.ToArray(),
            env: null,
            new DirectoryInfo(startInfo.WorkingDirectory),
            options);
    }

    private static IProcessExecution CreateExecution(
        FileInfo scriptFile,
        bool isolateConsole,
        IProcessTreeGracefulShutdownSignaler signaler,
        IGracefulShutdownWindow shutdownService)
    {
        // The Windows kill-on-close job is now resolved on-demand inside the factory via
        // WindowsConsoleProcessJob.Shared, so the test no longer creates or disposes one.
        return CreateExecution(
            scriptFile,
            new ProcessInvocationOptions
            {
                IsolateConsole = isolateConsole,
                GracefulShutdownSignaler = signaler,
                ShutdownService = shutdownService
            });
    }

}
