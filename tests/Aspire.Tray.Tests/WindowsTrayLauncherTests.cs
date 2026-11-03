// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Aspire.Tray.Tests.Helpers;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Tray.Tests;

public class WindowsTrayLauncherTests
{
    public static bool SupportsWindows => OperatingSystem.IsWindows();
    public static bool SupportsWindowsDesktop => OperatingSystem.IsWindows() && TestWindowsProcessJob.HasDesktopShell;

    [Theory(Skip = "Requires a Windows desktop shell.", SkipUnless = nameof(SupportsWindowsDesktop))]
    [InlineData(0x1800u, false)]
    [InlineData(0x2000u, false)]
    [InlineData(0x2000u, true)]
    [SupportedOSPlatform("windows")]
    public async Task DetachedChildSurvivesCallerAndItsJobs(uint callerJobFlags, bool nestedBreakawayJob)
    {
        using var callerJob = new TestWindowsProcessJob(callerJobFlags);
        using var innerJob = nestedBreakawayJob ? new TestWindowsProcessJob(0x2800) : null;
        var pipeName = $"aspire-tray-launch-test-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new RemoteInvokeOptions();
        options.StartInfo.CreateNoWindow = true;
        using var caller = RemoteExecutor.Invoke((Func<string, Task>)(async name =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            var signal = new byte[1];
            await pipe.ReadExactlyAsync(signal, timeout.Token);
            var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
            using var child = WindowsTrayLauncher.LaunchDetached(ping, $"\"{ping}\" -n 120 127.0.0.1", Environment.SystemDirectory);
            await WindowsTrayLauncher.CompleteStartupAsync(child, async () =>
            {
                // Hold the exact native child handle until the test owns a process handle.
                // Failed IPC uses the production cleanup path, so no child is left behind.
                var identity = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(identity, TestWindowsProcessJob.GetProcessId(child));
                await pipe.WriteAsync(identity, timeout.Token);
                await pipe.ReadExactlyAsync(signal, timeout.Token);
            });
        }), pipeName, options);
        Process? child = null;
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            callerJob.Assign(caller.Process.SafeHandle);
            innerJob?.Assign(caller.Process.SafeHandle);
            Assert.True(callerJob.Contains(caller.Process.SafeHandle));
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);

            var identity = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(identity, timeout.Token);
            child = Process.GetProcessById(BinaryPrimitives.ReadInt32LittleEndian(identity));
            var childHandle = child.SafeHandle;
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            await caller.Process.WaitForExitAsync(timeout.Token);

            Assert.False(callerJob.Contains(childHandle));
            if (innerJob is not null)
            {
                Assert.False(innerJob.Contains(childHandle));
            }
            innerJob?.Dispose();
            callerJob.Dispose();
            Assert.False(child.HasExited);
        }
        finally
        {
            if (child is not null)
            {
                using (child)
                {
                    await CliProcess.TerminateOwnedChildAsync(child);
                }
            }
        }
    }

    [Fact(Skip = "Requires a Windows desktop shell.", SkipUnless = nameof(SupportsWindowsDesktop))]
    [SupportedOSPlatform("windows")]
    public void MissingExecutablePreservesNativeLaunchError()
    {
        var executable = Path.Combine(Environment.SystemDirectory, $"aspire-tray-missing-{Guid.NewGuid():N}.exe");

        var error = Assert.Throws<Win32Exception>(() =>
            WindowsTrayLauncher.LaunchDetached(executable, $"\"{executable}\"", Environment.SystemDirectory));

        Assert.Equal(2, error.NativeErrorCode);
    }

    [Theory(Skip = "Uses Windows process handles.", SkipUnless = nameof(SupportsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task FailedReadinessWaitsForExactChildExitAndLeavesOtherProcessesRunning(bool rejected)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-t", "127.0.0.1" }
        };
        using var unrelated = Process.Start(start)!;
        using var child = Process.Start(start)!;
        Exception failure = rejected ? new InvalidOperationException("rejected") : new TimeoutException("timed out");
        try
        {
            var actual = await Assert.ThrowsAsync(failure.GetType(),
                () => WindowsTrayLauncher.CompleteStartupAsync(child.SafeHandle, () => Task.FromException(failure)));
            Assert.Same(failure, actual);
            Assert.True(child.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            await CliProcess.TerminateOwnedChildAsync(child);
            await CliProcess.TerminateOwnedChildAsync(unrelated);
        }
    }

    [Fact(Skip = "Uses Windows process handles.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public async Task AlreadyExitedChildPreservesTheReadinessFailure()
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "exit", "0" }
        })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // Acquire the handle before exit, just like the production CreateProcess path.
            var handle = child.SafeHandle;
            await child.WaitForExitAsync(timeout.Token);
            var failure = new IOException("disconnected");
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                WindowsTrayLauncher.CompleteStartupAsync(handle, () => Task.FromException(failure))));
            Assert.True(child.HasExited);
        }
        finally
        {
            await CliProcess.TerminateOwnedChildAsync(child);
        }
    }
}
