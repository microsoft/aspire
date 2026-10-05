// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scriptFile = await CreateLongRunningScriptAsync(workspace.WorkspaceRoot);

        var execution = CreateExecution(
            scriptFile,
            new ProcessInvocationOptions());

        await execution.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => execution.StartAsync(CancellationToken.None));
    }

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

        Assert.True(await execution.StartAsync(CancellationToken.None));

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

        Assert.True(await execution.StartAsync(CancellationToken.None));

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

        Assert.True(await execution.StartAsync(CancellationToken.None));

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

        Assert.True(await execution.StartAsync(CancellationToken.None));

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

        Assert.True(await execution.StartAsync(CancellationToken.None));

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

        Assert.True(await execution.StartAsync(CancellationToken.None));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        shutdownService.Expire();

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitForExitAsync(cts.Token));

        Assert.Single(signaler.Pids);
        Assert.True(WaitForProcessExit(execution.ProcessId, TimeSpan.FromSeconds(10)), $"Expected process {execution.ProcessId} to be killed after signaler failure.");
    }

    [Fact]
    public async Task WaitForExitAsync_CallbackThrows_StillDeliversRemainingOutputAndExitCode()
    {
        // The callback throws on the FIRST line; every later line must still be delivered so a
        // faulting consumer cannot leave the child blocked on a full pipe.
        var seenLines = new List<string>();
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/c", "echo line-one&echo line-two" })
            : ("/bin/sh", new[] { "-c", "echo line-one; echo line-two" });

        await using var execution = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance).CreateExecution(
            fileName,
            arguments,
            env: null,
            new DirectoryInfo(Environment.CurrentDirectory),
            new ProcessInvocationOptions
            {
                StandardOutputCallback = line =>
                {
                    seenLines.Add(line.Trim());
                    if (line.Contains("line-one"))
                    {
                        throw new InvalidOperationException("intentional callback failure");
                    }
                }
            });

        Assert.True(await execution.StartAsync(CancellationToken.None));

        Assert.Equal(0, await execution.WaitForExitAsync(CancellationToken.None).DefaultTimeout());
        Assert.Equal(["line-one", "line-two"], seenLines);
    }

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [SupportedOSPlatform("windows")]
    public void CreateProcessStartInfo_OnWindows_MapsLaunchOptions(
        bool isolateConsole,
        bool killOnParentExit,
        bool detached,
        bool expectedCreateNoWindow,
        bool expectedOnlyStandardHandlesInherited)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test.");

        var startInfo = ProcessExecutionFactory.CreateProcessStartInfo(
            "child.exe",
            ["arg"],
            Environment.CurrentDirectory,
            new ProcessInvocationOptions
            {
                IsolateConsole = isolateConsole,
                KillOnParentExit = killOnParentExit,
                Detached = detached,
            });

        Assert.Equal("child.exe", startInfo.FileName);
        Assert.Equal(["arg"], startInfo.ArgumentList);
        Assert.Equal(expectedCreateNoWindow, startInfo.CreateNoWindow);
        Assert.Equal(killOnParentExit, startInfo.KillOnParentExit);
        // DETACHED_PROCESS would leave the child without a console to receive CTRL+C.
        Assert.False(startInfo.StartDetached);
        if (expectedOnlyStandardHandlesInherited)
        {
            Assert.NotNull(startInfo.InheritedHandles);
            Assert.Empty(startInfo.InheritedHandles);
        }
        else
        {
            Assert.Null(startInfo.InheritedHandles);
        }

        Assert.False(startInfo.RedirectStandardInput);
        Assert.Equal(!detached, startInfo.RedirectStandardOutput);
        Assert.Equal(!detached, startInfo.RedirectStandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task StartAsync_OnWindows_IsolateConsole_ChildReceivesCtrlCThroughItsOwnConsole(bool killOnParentExit)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test.");

        // Mirror Program.Main: clear any inherited "ignore CTRL+C" attribute so the child, which
        // inherits it across CreateProcess, can observe CTRL_C_EVENT regardless of how the test host
        // was launched.
        WindowsProcessInterop.SetConsoleCtrlHandler(nint.Zero, add: false);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var execution = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance).CreateExecution(
            "ping.exe",
            ["-n", "120", "127.0.0.1"],
            env: null,
            new DirectoryInfo(Environment.CurrentDirectory),
            new ProcessInvocationOptions
            {
                IsolateConsole = true,
                KillOnParentExit = killOnParentExit,
                StandardOutputCallback = _ => started.TrySetResult()
            });

        Assert.True(await execution.StartAsync(CancellationToken.None));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var signal = await SendCtrlCThroughAttachedConsoleAsync(execution.ProcessId);
        Assert.True(signal.ExitStatus.ExitCode == 0, $"Signaler exited with {signal.ExitStatus.ExitCode}: {signal.StandardError}");

        // STATUS_CONTROL_C_EXIT: ping handled CTRL+C and exited, rather than being killed.
        Assert.Equal(unchecked((int)0xC000013A), await execution.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Performs the same console-signal sequence as DCP's <c>stop-process-tree</c> on Windows,
    /// from a separate process so the test host's console attachment is never changed:
    /// attach to the target's console, ignore CTRL+C in the signaler, and send CTRL_C_EVENT to
    /// every process attached to that console. AttachConsole fails if the target has no console.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task<ProcessTextOutput> SendCtrlCThroughAttachedConsoleAsync(int processId)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -Namespace AspireTests -Name ConsoleSignal -MemberDefinition @'
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
            '@
            [void][AspireTests.ConsoleSignal]::FreeConsole()
            if (-not [AspireTests.ConsoleSignal]::AttachConsole({{processId}})) { [Console]::Error.WriteLine("AttachConsole failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 2 }
            if (-not [AspireTests.ConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { [Console]::Error.WriteLine("SetConsoleCtrlHandler failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 3 }
            if (-not [AspireTests.ConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)) { [Console]::Error.WriteLine("GenerateConsoleCtrlEvent failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 4 }
            exit 0
            """;

        // -EncodedCommand avoids Windows PowerShell's command-line quote handling for the script.
        var startInfo = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))])
        {
            // DCP is launched the same way: its own hidden console, so it can FreeConsole/AttachConsole
            // without touching the console the test host is attached to.
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await Process.RunAndCaptureTextAsync(startInfo, timeout.Token);
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
