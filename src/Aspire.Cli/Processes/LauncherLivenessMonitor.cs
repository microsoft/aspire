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
    private readonly ParentProcessLivenessMonitor _monitor;

    private LauncherLivenessMonitor(ParentProcessLivenessMonitor monitor)
    {
        _monitor = monitor;
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

        var monitor = ParentProcessLivenessMonitor.Start(
            launcherPid,
            launcherStartedUnix,
            _ =>
            {
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

                return Task.CompletedTask;
            },
            timeProvider);

        return new LauncherLivenessMonitor(monitor);
    }

    public ValueTask DisposeAsync() => _monitor.DisposeAsync();
}
