// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.Processes;

public class WindowsConsoleProcessJobTests
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
