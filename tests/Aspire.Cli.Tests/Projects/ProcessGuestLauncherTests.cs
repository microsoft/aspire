// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class ProcessGuestLauncherTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task LaunchAsync_NoOptions_OnCancellation_ForceKillsProcessTreeAndReturns()
    {
        // Baseline: when no GuestLaunchOptions are passed (publish path, scaffolding, any caller
        // not opted into the central shutdown budget) the launcher must continue to force-kill on
        // cancellation. This preserves today's tactical behavior for non-Run callers and exercises
        // the same code path as the existing ProcessGuestLauncher_KillsProcessAndReturnsOnCancellation
        // test, just with an explicit assertion on the no-options branch.
        var launcher = new ProcessGuestLauncher("test", NullLogger<ProcessGuestLauncher>.Instance);

        var (command, args) = GetLongRunningCommand();

        using var cts = new CancellationTokenSource();
        var launchTask = launcher.LaunchAsync(
            command,
            args,
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            cts.Token);

        // Give the OS time to spawn the child before cancelling so we exercise the kill-while-running
        // path instead of the cancel-before-start short-circuit.
        await Task.Delay(500);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        var (exitCode, _) = await launchTask.WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.NotEqual(0, exitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected force-kill ladder to return within 15s of cancellation but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task LaunchAsync_WithGracefulServices_GracefulSucceeds_NoTreeKillEscalation()
    {
        // Run-path happy case: graceful signaler is invoked, simulates a successful signal by
        // killing the process directly, and the ladder observes the exit via WaitForExitAsync
        // before anyone calls Expire(). The central shutdown token must NOT be cancelled — this
        // proves the launcher consumes the token as a deadline (not a trigger) and doesn't burn
        // the central budget on successful graceful exits.
        var launcher = new ProcessGuestLauncher("test", NullLogger<ProcessGuestLauncher>.Instance);
        var (command, args) = GetLongRunningCommand();

        using var cts = new CancellationTokenSource();
        using var shutdownService = new GracefulShutdownService();
        var signaler = new RecordingGracefulSignaler(onSignal: pid =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already exited; treat as graceful success.
            }

            return Task.FromResult(true);
        });

        var options = new GuestLaunchOptions(
            IsolateConsoleForGracefulShutdown: false,
            GracefulShutdownSignaler: signaler,
            ShutdownService: shutdownService);

        var launchTask = launcher.LaunchAsync(
            command,
            args,
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            cts.Token,
            afterLaunchAsync: null,
            options: options);

        await Task.Delay(500);
        cts.Cancel();

        var (exitCode, _) = await launchTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, exitCode);
        Assert.Single(signaler.Pids);
        Assert.False(shutdownService.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task LaunchAsync_WithGracefulServices_BlockingSignalerDoesNotConsumeGracefulBudget()
    {
        // Regression coverage for DCP's stop-process-tree behavior: the graceful signaler can
        // deliver the signal quickly and then block until the target exits. The ladder must wait
        // for process exit in parallel with that signaler instead of awaiting the signaler first.
        var launcher = new ProcessGuestLauncher("test", NullLogger<ProcessGuestLauncher>.Instance);
        var (command, args) = GetLongRunningCommand();

        using var cts = new CancellationTokenSource();
        using var shutdownService = new GracefulShutdownService();
        var signalerNeverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaler = new RecordingGracefulSignaler(onSignal: pid =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already exited; treat as graceful success.
            }

            return signalerNeverCompletes.Task;
        });

        var options = new GuestLaunchOptions(
            IsolateConsoleForGracefulShutdown: false,
            GracefulShutdownSignaler: signaler,
            ShutdownService: shutdownService);

        var launchTask = launcher.LaunchAsync(
            command,
            args,
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            cts.Token,
            afterLaunchAsync: null,
            options: options);

        await Task.Delay(500);
        cts.Cancel();

        var stopwatch = Stopwatch.StartNew();
        var (exitCode, _) = await launchTask.WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.NotEqual(0, exitCode);
        Assert.Single(signaler.Pids);
        Assert.False(shutdownService.Token.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected process exit to win over the still-blocked signaler but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task LaunchAsync_WithGracefulServices_ProcessIgnoresSignal_ExpireEscalatesToTreeKill()
    {
        // Run-path bad-citizen case: graceful signaler accepts the request but the process ignores
        // it (the canonical tsx-swallows-Ctrl+Break scenario on Windows). Expiring the central token
        // must break the ladder out of WaitForExitAsync and escalate to Kill(entireProcessTree: true)
        // so the tree dies even when the cooperative path fails.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var launcher = new ProcessGuestLauncher("test", NullLogger<ProcessGuestLauncher>.Instance);
        var descendantPidFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "descendant.pid"));
        var (command, args) = await GetProcessTreeCommandAsync(workspace.WorkspaceRoot, descendantPidFile);

        using var cts = new CancellationTokenSource();
        using var shutdownService = new GracefulShutdownService();

        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaler = new RecordingGracefulSignaler(onSignal: _ =>
        {
            signaled.TrySetResult();
            // Pretend the OS signal was delivered but the process is a bad citizen and refuses to exit.
            return Task.FromResult(true);
        });

        var options = new GuestLaunchOptions(
            IsolateConsoleForGracefulShutdown: false,
            GracefulShutdownSignaler: signaler,
            ShutdownService: shutdownService);

        var launchTask = launcher.LaunchAsync(
            command,
            args,
            workspace.WorkspaceRoot,
            new Dictionary<string, string>(),
            cts.Token,
            afterLaunchAsync: null,
            options: options);

        var descendantPid = await WaitForPidFileAsync(descendantPidFile);

        try
        {
            cts.Cancel();

            // Wait until the ladder has actually called the signaler before expiring. Otherwise Expire()
            // could fire before the ladder reaches WaitForExitAsync and we'd be testing cancellation
            // ordering rather than the escalation path.
            await signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            shutdownService.Expire();

            var stopwatch = Stopwatch.StartNew();
            var (exitCode, _) = await launchTask.WaitAsync(TimeSpan.FromSeconds(30));
            stopwatch.Stop();

            Assert.NotEqual(0, exitCode);
            Assert.True(WaitForProcessExit(descendantPid, TimeSpan.FromSeconds(10)), $"Expected descendant process {descendantPid} to be killed with the root process tree.");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Expected escalation to tree-kill within 15s of Expire() but it took {stopwatch.Elapsed}.");
        }
        finally
        {
            TryKillProcess(descendantPid);
        }
    }

    [Fact]
    public async Task LaunchAsync_WithGracefulServices_SignalerThrows_StillEscalatesToTreeKill()
    {
        // Best-effort contract for the graceful signal: any exception (DCP layout discovery failed,
        // network blip, anything) must be logged and swallowed so the ladder still escalates to
        // Kill(entireProcessTree: true). The previous force-kill path swallowed signaler failures
        // implicitly by not even calling the signaler; the new ladder needs an explicit guarantee.
        var launcher = new ProcessGuestLauncher("test", NullLogger<ProcessGuestLauncher>.Instance);
        var (command, args) = GetLongRunningCommand();

        using var cts = new CancellationTokenSource();
        using var shutdownService = new GracefulShutdownService();

        var signaler = new RecordingGracefulSignaler(onSignal: _ =>
            throw new InvalidOperationException("simulated DCP failure"));

        var options = new GuestLaunchOptions(
            IsolateConsoleForGracefulShutdown: false,
            GracefulShutdownSignaler: signaler,
            ShutdownService: shutdownService);

        var launchTask = launcher.LaunchAsync(
            command,
            args,
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            cts.Token,
            afterLaunchAsync: null,
            options: options);

        await Task.Delay(500);
        cts.Cancel();
        // Expire immediately — without a working signaler there's nothing to wait for; the ladder
        // is going to wait on WaitForExitAsync(gracefulToken) and we want the escalation to fire.
        shutdownService.Expire();

        var stopwatch = Stopwatch.StartNew();
        var (exitCode, _) = await launchTask.WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.NotEqual(0, exitCode);
        Assert.Single(signaler.Pids);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected escalation to tree-kill within 15s of Expire() but it took {stopwatch.Elapsed}.");
    }

    private static (string Command, string[] Args) GetLongRunningCommand()
    {
        // Cross-platform child that runs long enough to outlive the cancellation+kill ladder under
        // realistic CI load. Matches LongRunningAppHostServerProject (AppHostServerSessionTests).
        if (OperatingSystem.IsWindows())
        {
            // ping with a long count keeps the child alive ~60s; tree-kill needs to traverse cmd -> ping.
            return ("cmd.exe", ["/c", "ping", "-n", "60", "127.0.0.1"]);
        }

        return ("sleep", ["60"]);
    }

    private static async Task<(string Command, string[] Args)> GetProcessTreeCommandAsync(DirectoryInfo workspaceRoot, FileInfo descendantPidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "spawn-descendant.ps1"));
            var content =
                "$child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 60' -PassThru" + Environment.NewLine +
                $"Set-Content -Path '{descendantPidFile.FullName}' -Value $child.Id" + Environment.NewLine +
                "Wait-Process -Id $child.Id" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptFile.FullName, content);

            return ("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptFile.FullName]);
        }

        var shellFile = new FileInfo(Path.Combine(workspaceRoot.FullName, "spawn-descendant.sh"));
        var shellContent =
            "#!/usr/bin/env bash" + Environment.NewLine +
            "sleep 60 &" + Environment.NewLine +
            $"echo $! > \"{descendantPidFile.FullName}\"" + Environment.NewLine +
            "wait $!" + Environment.NewLine;
        await File.WriteAllTextAsync(shellFile.FullName, shellContent);
        File.SetUnixFileMode(
            shellFile.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return ("/bin/bash", [shellFile.FullName]);
    }

    private static async Task<int> WaitForPidFileAsync(FileInfo pidFile)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (pidFile.Exists)
            {
                var text = await File.ReadAllTextAsync(pidFile.FullName);
                if (int.TryParse(text.Trim(), out var pid))
                {
                    return pid;
                }
            }

            await Task.Delay(50);
            pidFile.Refresh();
        }

        throw new TimeoutException($"Timed out waiting for descendant pid file '{pidFile.FullName}'.");
    }

    private static bool WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessExited(pid))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static bool IsProcessExited(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return probe.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryKillProcess(int pid)
    {
        if (IsProcessExited(pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

}
