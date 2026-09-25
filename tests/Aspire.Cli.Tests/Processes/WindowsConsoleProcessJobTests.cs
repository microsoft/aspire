// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Processes;

public class WindowsConsoleProcessJobTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Constructor_OnWindows_SucceedsAndExposesValidHandle()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        using var job = new WindowsConsoleProcessJob();

        Assert.NotNull(job.Handle);
        Assert.False(job.Handle.IsInvalid);
        Assert.False(job.Handle.IsClosed);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Dispose_KillsAssignedChildProcess()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var job = new WindowsConsoleProcessJob();
        using var spawnedProcess = SpawnJobAssignedChildProcess(job, createNewConsole: true);

        // Confirm the child is up before disposing the job — otherwise a fast spawn
        // failure would look identical to successful parent-exit cleanup.
        Assert.False(spawnedProcess.HasExited);

        job.Dispose();

        // KILL_ON_JOB_CLOSE is reliably observable within a couple of seconds;
        // give a generous window for CI under load.
        await spawnedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(spawnedProcess.HasExited);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Dispose_KillsAssignedChildProcessWithoutConsoleIsolation()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var job = new WindowsConsoleProcessJob();
        using var spawnedProcess = SpawnJobAssignedChildProcess(job, createNewConsole: false);

        Assert.False(spawnedProcess.HasExited);

        job.Dispose();

        await spawnedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(spawnedProcess.HasExited);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SpawnProcess_WithJob_AssignsChildToJobAtomicallyAtCreation()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        using var job = new WindowsConsoleProcessJob();

        using var nulHandle = WindowsProcessInterop.CreateFileW(
            "NUL",
            WindowsProcessInterop.GenericRead | WindowsProcessInterop.GenericWrite,
            WindowsProcessInterop.FileShareRead | WindowsProcessInterop.FileShareWrite,
            nint.Zero,
            WindowsProcessInterop.OpenExisting,
            0,
            nint.Zero);

        Assert.False(nulHandle.IsInvalid);
        Assert.True(WindowsProcessInterop.SetHandleInformation(
            nulHandle,
            WindowsProcessInterop.HandleFlagInherit,
            WindowsProcessInterop.HandleFlagInherit));

        var nulRawHandle = nulHandle.DangerousGetHandle();
        var stdio = new WindowsProcessInterop.StdioHandles(
            Stdin: nulRawHandle,
            Stdout: nulRawHandle,
            Stderr: nulRawHandle);

        var pi = WindowsProcessInterop.SpawnProcess(
            "cmd.exe",
            ["/c", "ping", "-n", "60", "127.0.0.1"],
            Environment.CurrentDirectory,
            stdio,
            environment: null,
            createNewConsole: false,
            job.Handle);

        try
        {
            // PROC_THREAD_ATTRIBUTE_JOB_LIST associates the child with the job before it runs, so the
            // membership is observable the instant CreateProcess returns — there is no separate assign
            // step that a parent dying mid-spawn could skip, and the child was never suspended.
            Assert.True(WindowsProcessInterop.IsProcessInJob(pi.hProcess, job.Handle, out var isInJob));
            Assert.True(isInJob);
        }
        finally
        {
            WindowsProcessInterop.TerminateProcess(pi.hProcess, 1);
            WindowsProcessInterop.CloseHandle(pi.hProcess);
            WindowsProcessInterop.CloseHandle(pi.hThread);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DotNetRunCleanupChild_SurvivesOnlyWhenOuterJobIsOmitted()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("cleanup-probe");
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "CleanupProbe.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Program.cs"), """
            using System.Diagnostics;
            using System.Globalization;

            var markerPath = args[0];
            var pidPath = args[1];
            var releasePath = args[2];
            if (args is [_, _, _, "cleanup"])
            {
                await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                while (!File.Exists(releasePath))
                {
                    await Task.Delay(20);
                }

                await File.WriteAllTextAsync(markerPath, "cleanup complete");
                return;
            }

            var childStartInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
            };
            childStartInfo.ArgumentList.Add(markerPath);
            childStartInfo.ArgumentList.Add(pidPath);
            childStartInfo.ArgumentList.Add(releasePath);
            childStartInfo.ArgumentList.Add("cleanup");

            using var child = Process.Start(childStartInfo)
                ?? throw new InvalidOperationException("Failed to start cleanup child.");
            """);

        await BuildCleanupProbeAsync(projectFile);

        await RunCleanupProbeAsync(projectFile, workspace, assignOuterJob: false, expectCleanup: true);
        await RunCleanupProbeAsync(projectFile, workspace, assignOuterJob: true, expectCleanup: false);
    }

    private static async Task BuildCleanupProbeAsync(FileInfo projectFile)
    {
        var startInfo = new ProcessStartInfo(ProcessTestHelpers.GetDotNetExecutablePath())
        {
            WorkingDirectory = projectFile.DirectoryName!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile.FullName);
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to build cleanup probe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(
                process.ExitCode == 0,
                $"Cleanup probe build failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunCleanupProbeAsync(
        FileInfo projectFile,
        TemporaryWorkspace workspace,
        bool assignOuterJob,
        bool expectCleanup)
    {
        var runName = assignOuterJob ? "with-outer-job" : "without-outer-job";
        var markerPath = Path.Combine(workspace.Path, $"{runName}.complete");
        var pidPath = Path.Combine(workspace.Path, $"{runName}.pid");
        var releasePath = Path.Combine(workspace.Path, $"{runName}.release");
        var cleanupPid = 0;
        using WindowsConsoleProcessJob? outerJob = assignOuterJob ? new WindowsConsoleProcessJob() : null;

        using var nulHandle = WindowsProcessInterop.CreateFileW(
            "NUL",
            WindowsProcessInterop.GenericRead | WindowsProcessInterop.GenericWrite,
            WindowsProcessInterop.FileShareRead | WindowsProcessInterop.FileShareWrite,
            nint.Zero,
            WindowsProcessInterop.OpenExisting,
            0,
            nint.Zero);

        Assert.False(nulHandle.IsInvalid);
        Assert.True(WindowsProcessInterop.SetHandleInformation(
            nulHandle,
            WindowsProcessInterop.HandleFlagInherit,
            WindowsProcessInterop.HandleFlagInherit));

        var nulRawHandle = nulHandle.DangerousGetHandle();
        var stdio = new WindowsProcessInterop.StdioHandles(
            Stdin: nulRawHandle,
            Stdout: nulRawHandle,
            Stderr: nulRawHandle);

        var pi = WindowsProcessInterop.SpawnProcess(
            ProcessTestHelpers.GetDotNetExecutablePath(),
            ["run", "--project", projectFile.FullName, "--no-build", "--no-restore", "--", markerPath, pidPath, releasePath],
            projectFile.DirectoryName!,
            stdio,
            environment: null,
            createNewConsole: true,
            outerJob?.Handle);

        var runProcessExited = false;
        try
        {
            using var runProcess = Process.GetProcessById(pi.dwProcessId);
            await runProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            runProcessExited = true;
            Assert.Equal(0, runProcess.ExitCode);

            cleanupPid = await ProcessTestHelpers.WaitForProcessIdAsync(pidPath, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(ProcessTestHelpers.IsProcessExited(cleanupPid));

            outerJob?.Dispose();

            if (expectCleanup)
            {
                await File.WriteAllTextAsync(releasePath, string.Empty, TestContext.Current.CancellationToken);
                await ProcessTestHelpers.WaitForFileAsync(markerPath, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(
                    ProcessTestHelpers.WaitForProcessExit(cleanupPid, TimeSpan.FromSeconds(15)),
                    "The cleanup child did not exit after writing its completion marker.");
                Assert.Equal("cleanup complete", await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.True(
                    ProcessTestHelpers.WaitForProcessExit(cleanupPid, TimeSpan.FromSeconds(15)),
                    "The outer kill-on-close job did not terminate the cleanup child.");
                Assert.False(File.Exists(markerPath));
            }
        }
        finally
        {
            if (!runProcessExited)
            {
                ProcessTestHelpers.TryKillProcess(pi.dwProcessId);
            }
            WindowsProcessInterop.CloseHandle(pi.hProcess);
            WindowsProcessInterop.CloseHandle(pi.hThread);
            if (cleanupPid != 0)
            {
                ProcessTestHelpers.TryKillProcess(cleanupPid);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static Process SpawnJobAssignedChildProcess(WindowsConsoleProcessJob job, bool createNewConsole)
    {
        using var nulHandle = WindowsProcessInterop.CreateFileW(
            "NUL",
            WindowsProcessInterop.GenericRead | WindowsProcessInterop.GenericWrite,
            WindowsProcessInterop.FileShareRead | WindowsProcessInterop.FileShareWrite,
            nint.Zero,
            WindowsProcessInterop.OpenExisting,
            0,
            nint.Zero);

        Assert.False(nulHandle.IsInvalid);
        Assert.True(WindowsProcessInterop.SetHandleInformation(
            nulHandle,
            WindowsProcessInterop.HandleFlagInherit,
            WindowsProcessInterop.HandleFlagInherit));

        var nulRawHandle = nulHandle.DangerousGetHandle();
        var stdio = new WindowsProcessInterop.StdioHandles(
            Stdin: nulRawHandle,
            Stdout: nulRawHandle,
            Stderr: nulRawHandle);

        var pi = WindowsProcessInterop.SpawnProcess(
            "cmd.exe",
            ["/c", "ping", "-n", "60", "127.0.0.1"],
            Environment.CurrentDirectory,
            stdio,
            environment: null,
            createNewConsole,
            job.Handle);

        try
        {
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            WindowsProcessInterop.CloseHandle(pi.hProcess);
            WindowsProcessInterop.CloseHandle(pi.hThread);
        }
    }
}
