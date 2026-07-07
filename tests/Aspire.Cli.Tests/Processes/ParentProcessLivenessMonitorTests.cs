// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Processes;

public class ParentProcessLivenessMonitorTests
{
    private static readonly TimeSpan s_observationTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ParentAlreadyExited_InvokesCallbackWithoutWaitingForFirstTick()
    {
        // Immediate-first-check: when the parent is already gone at Start time, the callback must fire on
        // the pre-tick probe rather than after a full poll interval. A FakeTimeProvider that is never
        // advanced proves it: with the immediate probe the callback still fires; if detection depended on
        // the timer, WaitForNextTickAsync would never complete and this would time out.
        using var parent = StartLongRunningProcess();
        var parentStartedUnix = GetProcessStartTimeUnixSeconds(parent);
        var parentPid = parent.Id;

        parent.Kill(entireProcessTree: true);
        parent.WaitForExit();

        var timeProvider = new FakeTimeProvider();
        var exitedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = ParentProcessLivenessMonitor.Start(
            parentPid,
            parentStartedUnix,
            _ =>
            {
                exitedTcs.TrySetResult();
                return Task.CompletedTask;
            },
            timeProvider);

        // Deliberately never advance the fake clock. The callback must still fire from the initial probe.
        await exitedTcs.Task.WaitAsync(s_observationTimeout);
    }

    [Fact]
    public async Task ParentExit_InvokesCallback()
    {
        using var parent = StartLongRunningProcess();
        var parentStartedUnix = GetProcessStartTimeUnixSeconds(parent);

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
        var parentStartedUnix = ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds();

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
        var parentStartedUnix = GetProcessStartTimeUnixSeconds(parent);

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

    [Fact]
    public async Task ParentExit_CallbackFailureDoesNotFaultDispose()
    {
        using var parent = StartLongRunningProcess();
        var parentStartedUnix = GetProcessStartTimeUnixSeconds(parent);

        var callbackInvokedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = ParentProcessLivenessMonitor.Start(
            parent.Id,
            parentStartedUnix,
            _ =>
            {
                callbackInvokedTcs.TrySetResult();
                throw new InvalidOperationException("Callback failure should not fault monitor disposal.");
            });

        try
        {
            parent.Kill(entireProcessTree: true);
            parent.WaitForExit();

            await callbackInvokedTcs.Task.WaitAsync(s_observationTimeout);

            var exception = await Record.ExceptionAsync(async () => await monitor.DisposeAsync());

            Assert.Null(exception);
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
                parent.WaitForExit();
            }

            await monitor.DisposeAsync();
        }
    }

    private static Process StartLongRunningProcess() => TestProcesses.StartLongRunning();

    private static long GetProcessStartTimeUnixSeconds(Process process)
    {
        var startTime = ProcessStartTimeHelper.TryGetProcessStartTimeUnixSeconds(process.Id);
        Assert.NotNull(startTime);
        return startTime.Value;
    }
}
