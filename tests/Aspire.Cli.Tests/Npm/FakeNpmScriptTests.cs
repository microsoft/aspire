// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Npm;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Npm;

public class FakeNpmScriptTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void IsExpectedHolderProcessName_OnUnix_AcceptsHolderScriptBasename()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix-only test.");

        using var fakeNpm = FakeNpmScript.BuildExitThenHoldOutputOpen(outputHelper, "13.4.6");
        var holderScriptName = Path.GetFileName(fakeNpm.HolderScriptPath);

        Assert.True(FakeNpmScript.IsExpectedHolderProcessName(holderScriptName, fakeNpm.HolderScriptPath));
        Assert.True(FakeNpmScript.IsExpectedHolderProcessName(fakeNpm.HolderScriptPath, fakeNpm.HolderScriptPath));
        Assert.False(FakeNpmScript.IsExpectedHolderProcessName($"{holderScriptName}.bak", fakeNpm.HolderScriptPath));
    }

    [Fact]
    public async Task ReleaseAsync_OnUnixIgnoreRelease_UsesHolderControlledForceExit()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix-only test.");

        using var fakeNpm = FakeNpmScript.BuildExitThenIgnoreRelease(outputHelper, "13.4.6");
        using var process = StartFakeNpm(fakeNpm);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await fakeNpm.WaitForParentExitAsync().DefaultTimeout();

        await fakeNpm.ReleaseAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(
            File.Exists(fakeNpm.HolderForceExitAcknowledgedFile),
            "Unix cleanup should request holder-controlled exit instead of killing by PID after release.");
    }

    [Fact]
    public async Task ReleaseAsync_OnWindowsIgnoreReleaseAndForceExit_KillsObservedHolderWithoutKillingSameNamedVictim()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test.");

        using var fakeNpm = FakeNpmScript.BuildExitThenIgnoreReleaseAndForceExit(outputHelper, "13.4.6");
        using var process = StartFakeNpm(fakeNpm);
        using var victimProcess = StartExpectedHolderNamedVictimProcess();

        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            await fakeNpm.WaitForParentExitAsync().DefaultTimeout();

            var observedHolderProcess = await fakeNpm.WaitForHolderProcessAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            await File.WriteAllTextAsync(
                fakeNpm.HolderIdentityFile,
                victimProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            await fakeNpm.ReleaseAsync(TestContext.Current.CancellationToken).DefaultTimeout();

            Assert.True(
                observedHolderProcess.WaitForExit((int)TestConstants.DefaultTimeoutTimeSpan.TotalMilliseconds),
                "Fallback cleanup did not terminate the original holder process.");
            Assert.False(victimProcess.HasExited, "Fallback cleanup killed the same-named victim process.");
        }
        finally
        {
            await EnsureProcessStoppedAsync(victimProcess);
        }
    }

    [Fact]
    public async Task WaitForParentExitAsync_CompletesOnlyAfterNpmShellExits()
    {
        using var fakeNpm = FakeNpmScript.BuildWaitForParentReleaseThenHoldOutputOpen(outputHelper, "13.4.6");
        using var process = StartFakeNpm(fakeNpm);

        await fakeNpm.WaitForParentReadyAsync().DefaultTimeout();
        var waitForParentExitTask = fakeNpm.WaitForParentExitAsync();

        Assert.False(waitForParentExitTask.IsCompleted, "Parent-exit signal fired before the npm shell exited.");

        await fakeNpm.ReleaseParentExitAsync().DefaultTimeout();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await waitForParentExitTask.DefaultTimeout();
    }

    [Fact]
    public async Task WaitForHolderProcessAsync_WhenProcessLaunchIsDelayedAfterApplyEnvironment_ReturnsObservedHolderProcess()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenHoldOutputOpen(outputHelper, "13.4.6");
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            fakeNpm.ScriptPath,
            ["view", "@microsoft/aspire-cli@latest", "version"],
            fakeNpm.Workspace.WorkspaceRoot.FullName,
            new TestEnvironment());
        fakeNpm.ApplyEnvironment(startInfo);

        // Hold launch beyond the observation bound to prove holder discovery does not start before
        // the fake npm process has actually been started.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        Assert.True(process.Start());

        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await fakeNpm.WaitForParentExitAsync().DefaultTimeout();

        var holderProcessId = ReadProcessId(fakeNpm.HolderPidFile);
        var observedHolderProcess = await fakeNpm.WaitForHolderProcessAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(holderProcessId, observedHolderProcess.Id);
    }

    [Fact]
    public async Task WaitForHolderProcessAsync_WhenIdentityFileExistsButIsInitiallyEmpty_ReturnsObservedHolderProcess()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenHoldOutputOpen(outputHelper, "13.4.6");
        await File.WriteAllTextAsync(fakeNpm.HolderIdentityFile, string.Empty, TestContext.Current.CancellationToken);

        using var process = StartFakeNpm(fakeNpm);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await fakeNpm.WaitForParentExitAsync().DefaultTimeout();

        var holderProcessId = ReadProcessId(fakeNpm.HolderPidFile);
        var observedHolderProcess = await fakeNpm.WaitForHolderProcessAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(holderProcessId, observedHolderProcess.Id);
    }

    [Fact]
    public async Task WaitForHolderProcessAsync_WhenHolderIdentityChangesToSameNamedVictim_ReturnsOriginalHolderProcess()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenHoldOutputOpen(outputHelper, "13.4.6");
        using var process = StartFakeNpm(fakeNpm);
        using var victimProcess = StartExpectedHolderNamedVictimProcess();

        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            await fakeNpm.WaitForParentExitAsync().DefaultTimeout();

            var holderProcessId = ReadProcessId(fakeNpm.HolderPidFile);
            await File.WriteAllTextAsync(
                fakeNpm.HolderIdentityFile,
                victimProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            var observedHolderProcess = await fakeNpm.WaitForHolderProcessAsync(TestContext.Current.CancellationToken).DefaultTimeout();

            Assert.Equal(holderProcessId, observedHolderProcess.Id);
            Assert.NotEqual(victimProcess.Id, observedHolderProcess.Id);
        }
        finally
        {
            await EnsureProcessStoppedAsync(victimProcess);
        }
    }

    [Fact]
    public async Task ReleaseAsync_WhenHolderPidChangesAfterRelease_DoesNotKillVictimProcess()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenPublishMutablePidOnRelease(outputHelper, "13.4.6");
        using var process = StartFakeNpm(fakeNpm);
        using var victimProcess = StartVictimProcess();

        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            await fakeNpm.WaitForParentExitAsync().DefaultTimeout();
            await File.WriteAllTextAsync(
                fakeNpm.HolderReleasePidFile,
                victimProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            await fakeNpm.ReleaseAsync(TestContext.Current.CancellationToken).DefaultTimeout();

            Assert.False(victimProcess.HasExited, "Cleanup killed the victim process after the holder rewrote its PID on release.");
        }
        finally
        {
            await EnsureProcessStoppedAsync(victimProcess);
        }
    }

    [Fact]
    public async Task ReleaseAsync_WhenHolderIdentityCannotBeValidated_DoesNotKillVictimProcess()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenHoldOutputOpen(outputHelper, "13.4.6");
        using var process = StartFakeNpm(fakeNpm);
        using var victimProcess = StartVictimProcess();

        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            // Wait only for the marker here. WaitForParentExitAsync also caches the verified holder,
            // which would bypass the intentionally corrupted identity file below.
            await fakeNpm.WaitForParentExitMarkerAsync().DefaultTimeout();
            await File.WriteAllTextAsync(
                fakeNpm.HolderIdentityFile,
                "not-a-valid-holder-pid",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                fakeNpm.HolderPidFile,
                victimProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            await fakeNpm.ReleaseAsync(TestContext.Current.CancellationToken).DefaultTimeout();

            Assert.False(victimProcess.HasExited, "Cleanup killed the victim process after holder identity validation failed.");
        }
        finally
        {
            await EnsureProcessStoppedAsync(victimProcess);
        }
    }

    [Fact]
    public async Task Dispose_KillsExactHolderProcessBeforeDeletingWorkspace()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenIgnoreRelease(outputHelper, "13.4.6");
        var workspacePath = fakeNpm.Workspace.WorkspaceRoot.FullName;
        using var process = StartFakeNpm(fakeNpm);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await fakeNpm.WaitForParentExitAsync().DefaultTimeout();
        var holderProcessId = (await fakeNpm.WaitForHolderProcessAsync().DefaultTimeout()).Id;
        using var observedHolderProcess = Process.GetProcessById(holderProcessId);

        fakeNpm.Dispose();

        Assert.True(
            observedHolderProcess.WaitForExit((int)TestConstants.DefaultTimeoutTimeSpan.TotalMilliseconds),
            "Holder process remained alive after cleanup.");
        Assert.False(Directory.Exists(workspacePath), $"Workspace was not deleted: {workspacePath}");
    }

    private static Process StartFakeNpm(FakeNpmScript fakeNpm)
    {
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            fakeNpm.ScriptPath,
            ["view", "@microsoft/aspire-cli@latest", "version"],
            fakeNpm.Workspace.WorkspaceRoot.FullName,
            new TestEnvironment());
        fakeNpm.ApplyEnvironment(startInfo);

        var process = new Process
        {
            StartInfo = startInfo
        };

        Assert.True(process.Start());
        return process;
    }

    private static Process StartExpectedHolderNamedVictimProcess()
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add("while :; do sleep 1; done");
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        Assert.True(process.Start());
        return process;
    }

    private static Process StartVictimProcess()
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping -n 30 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sleep",
                Arguments = "30",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        Assert.True(process.Start());
        return process;
    }

    private static int ReadProcessId(string pidFilePath)
    {
        return int.Parse(
            File.ReadAllText(pidFilePath),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task EnsureProcessStoppedAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
    }
}
