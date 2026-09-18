// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace Aspire.Tray.Tests;

public class WindowsTrayLauncherTests
{
    public static bool SupportsWindows => OperatingSystem.IsWindows();

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
