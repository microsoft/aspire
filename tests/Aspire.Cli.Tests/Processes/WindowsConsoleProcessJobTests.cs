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
        Process spawnedProcess;

        // Long-running ping (~60s) so we can verify it's still alive between spawn and
        // job dispose; the process exits exactly because JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        // fires when the last handle to the job closes (i.e. inside Dispose below).
        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = Environment.CurrentDirectory,
            JobHandle = job.Handle,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");

        await using (var child = IsolatedProcess.Start(
            startInfo,
            standardOutputHandler: static (_, _) => { },
            standardErrorHandler: static (_, _) => { }))
        {
            spawnedProcess = child.Process;

            // Confirm the child is up before disposing the job — otherwise a fast spawn
            // failure would look identical to a successful kill-on-close.
            Assert.False(spawnedProcess.HasExited);

            job.Dispose();

            // KILL_ON_JOB_CLOSE is reliably observable within a couple of seconds;
            // give a generous window for CI under load.
            await spawnedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(spawnedProcess.HasExited);
        }
    }
}

