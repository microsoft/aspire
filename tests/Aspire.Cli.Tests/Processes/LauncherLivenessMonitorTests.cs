// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Processes;

public class LauncherLivenessMonitorTests
{
    private static readonly TimeSpan s_observationTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void StartIfConfigured_WithoutLauncherConfig_ReturnsNull()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var runCts = new CancellationTokenSource();

        var monitor = LauncherLivenessMonitor.StartIfConfigured(configuration, runCts, TimeProvider.System, NullLogger.Instance);

        Assert.Null(monitor);
        Assert.False(runCts.IsCancellationRequested);
    }

    [Fact]
    public async Task LauncherExit_CancelsRun()
    {
        using var launcher = StartLongRunningProcess();
        var configuration = CreateLauncherConfiguration(launcher);
        using var runCts = new CancellationTokenSource();

        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = runCts.Token.Register(() => cancelTcs.TrySetResult());

        await using var monitor = LauncherLivenessMonitor.StartIfConfigured(configuration, runCts, TimeProvider.System, NullLogger.Instance);
        Assert.NotNull(monitor);

        // The launcher dies while the monitor is armed: the run must be cancelled so the AppHost is torn down.
        launcher.Kill(entireProcessTree: true);
        launcher.WaitForExit();

        await cancelTcs.Task.WaitAsync(s_observationTimeout);
        Assert.True(runCts.IsCancellationRequested);
    }

    [Fact]
    public async Task LauncherAlive_DoesNotCancelRun()
    {
        // The current process stands in for a launcher that stays alive through startup.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_LAUNCHER_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                ["ASPIRE_LAUNCHER_STARTED"] = ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds().ToString(CultureInfo.InvariantCulture),
            })
            .Build();
        using var runCts = new CancellationTokenSource();

        await using (var monitor = LauncherLivenessMonitor.StartIfConfigured(configuration, runCts, TimeProvider.System, NullLogger.Instance))
        {
            Assert.NotNull(monitor);

            // Give the monitor several poll intervals to (incorrectly) fire.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(runCts.IsCancellationRequested);
        }

        Assert.False(runCts.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_DisarmsMonitor()
    {
        using var launcher = StartLongRunningProcess();
        var configuration = CreateLauncherConfiguration(launcher);
        using var runCts = new CancellationTokenSource();

        var monitor = LauncherLivenessMonitor.StartIfConfigured(configuration, runCts, TimeProvider.System, NullLogger.Instance);
        Assert.NotNull(monitor);

        // Disarm first (mimicking startup reaching readiness), then kill the launcher. A disarmed monitor
        // must not cancel the run when the launcher later exits normally.
        await monitor.DisposeAsync();

        launcher.Kill(entireProcessTree: true);
        launcher.WaitForExit();

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(runCts.IsCancellationRequested);
    }

    private static IConfiguration CreateLauncherConfiguration(Process launcher)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_LAUNCHER_PID"] = launcher.Id.ToString(CultureInfo.InvariantCulture),
                ["ASPIRE_LAUNCHER_STARTED"] = GetProcessStartTimeUnixSeconds(launcher).ToString(CultureInfo.InvariantCulture),
            })
            .Build();
    }

    private static Process StartLongRunningProcess() => TestProcesses.StartLongRunning();

    private static long GetProcessStartTimeUnixSeconds(Process process)
    {
        var startTime = ProcessStartTimeHelper.TryGetProcessStartTimeUnixSeconds(process.Id);
        Assert.NotNull(startTime);
        return startTime.Value;
    }
}
