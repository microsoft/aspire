// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// Watches the foreground launcher process of a detached <c>aspire start</c> / <c>aspire run --detach</c>
/// and cancels the AppHost run if that launcher dies before the app reaches readiness.
/// </summary>
/// <remarks>
/// <para>
/// A detached child CLI is the supervisor that runs the AppHost for its whole lifetime; it is meant to
/// outlive the foreground that spawned it. But during the startup window (build + waiting for the app to
/// become ready) the child has no anchor to the launcher, so if the launcher is killed mid-start 
/// — e.g. the user gets impatient and hits Ctrl-C, or a test runner times it out — 
/// the AppHost + dashboard are leaked as orphaned processes.
/// </para>
/// </remarks>
internal sealed class LauncherLivenessMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _monitorTask;
    private int _disposed;

    private LauncherLivenessMonitor(
        int launcherPid,
        long? launcherStartedUnix,
        CancellationTokenSource cancelOnLauncherExit,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _monitorTask = MonitorAsync(launcherPid, launcherStartedUnix, cancelOnLauncherExit, timeProvider, logger, _stopCts.Token);
    }

    /// <summary>
    /// Starts a monitor when the launcher identity is present in configuration (i.e. this process is a detached child). 
    /// Returns <see langword="null"/> for a normal foreground run, where there is no launcher process to watch.
    /// </summary>
    public static LauncherLivenessMonitor? StartIfConfigured(
        IConfiguration configuration,
        CancellationTokenSource cancelOnLauncherExit,
        TimeProvider timeProvider,
        ILogger logger)
    {
        if (!int.TryParse(configuration[KnownConfigNames.CliLauncherProcessId], NumberStyles.Integer, CultureInfo.InvariantCulture, out var launcherPid))
        {
            return null;
        }

        var launcherStartedUnix = ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(configuration[KnownConfigNames.CliLauncherProcessStarted]);
        logger.LogDebug("Detached child: watching launcher process {LauncherPid} until the AppHost is ready.", launcherPid);
        return new LauncherLivenessMonitor(launcherPid, launcherStartedUnix, cancelOnLauncherExit, timeProvider, logger);
    }

    private static async Task MonitorAsync(
        int launcherPid,
        long? launcherStartedUnix,
        CancellationTokenSource cancelOnLauncherExit,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken stopToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
            while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false))
            {
                if (ProcessStartTimeHelper.IsProcessRunning(launcherPid, launcherStartedUnix))
                {
                    continue;
                }

                logger.LogWarning(
                    "Launcher process {LauncherPid} exited before the AppHost reached readiness. Shutting the detached AppHost down to avoid leaking it.",
                    launcherPid);

                try
                {
                    if (!cancelOnLauncherExit.IsCancellationRequested)
                    {
                        cancelOnLauncherExit.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The run already completed and disposed its cancellation source; nothing to do.
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Disarmed because the app reached readiness (the expected, common case).
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the run command's happy path disposes once at readiness and the outer finally is a
        // backstop, so guard against a second Cancel on an already-disposed source.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopCts.Cancel();
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping the monitor loop.
        }
        finally
        {
            _stopCts.Dispose();
        }
    }
}
