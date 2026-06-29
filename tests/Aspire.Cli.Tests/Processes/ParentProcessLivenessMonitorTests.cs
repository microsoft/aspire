// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Tests.Processes;

public class ParentProcessLivenessMonitorTests
{
    private static readonly TimeSpan s_observationTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ParentExit_InvokesCallback()
    {
        using var parent = StartLongRunningProcess();
        var parentStartedUnix = ((DateTimeOffset)parent.StartTime).ToUnixTimeSeconds();

        var exitedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = ParentProcessLivenessMonitor.Start(
            parent.Id,
            parentStartedUnix,
            _ =>
            {
                exitedTcs.TrySetResult();
                return Task.CompletedTask;
            });

        parent.Kill(entireProcessTree: true);
        parent.WaitForExit();

        await exitedTcs.Task.WaitAsync(s_observationTimeout);
    }

    [Fact]
    public async Task ParentAlive_DoesNotInvokeCallback()
    {
        // The current process stands in for a parent that stays alive.
        var parentStartedUnix = ((DateTimeOffset)Process.GetCurrentProcess().StartTime).ToUnixTimeSeconds();

        var invoked = false;
        await using (var monitor = ParentProcessLivenessMonitor.Start(
            Environment.ProcessId,
            parentStartedUnix,
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            }))
        {
            // Give the monitor several poll intervals to (incorrectly) fire.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(invoked);
        }

        Assert.False(invoked);
    }

    [Fact]
    public async Task Dispose_DisarmsBeforeParentExit()
    {
        using var parent = StartLongRunningProcess();
        var parentStartedUnix = ((DateTimeOffset)parent.StartTime).ToUnixTimeSeconds();

        var invoked = false;
        var monitor = ParentProcessLivenessMonitor.Start(
            parent.Id,
            parentStartedUnix,
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            });

        // Disarm first, then kill the parent. A disposed monitor must not invoke the callback.
        await monitor.DisposeAsync();

        parent.Kill(entireProcessTree: true);
        parent.WaitForExit();

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(invoked);
    }

    private static Process StartLongRunningProcess()
    {
        // Mirrors CliOrphanDetectorTests' cross-platform process choice for CI reliability.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t localhost") { CreateNoWindow = true }
            : new ProcessStartInfo("tail", "-f /dev/null") { CreateNoWindow = true };

        var process = Process.Start(psi);
        Assert.NotNull(process);
        return process;
    }
}
