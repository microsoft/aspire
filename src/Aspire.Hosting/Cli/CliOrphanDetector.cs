// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Cli;

internal sealed class CliOrphanDetector(IConfiguration configuration, IHostApplicationLifetime lifetime, TimeProvider timeProvider, ILogger<CliOrphanDetector> logger) : BackgroundService
{
    internal Func<int, bool> IsProcessRunning { get; set; } = (int pid) =>
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, null, logger);
        return process is not null;
    };

    internal Func<int, long, bool> IsProcessRunningWithStartTime { get; set; } = (int pid, long expectedStartTimeUnix) =>
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, DateTimeOffset.FromUnixTimeSeconds(expectedStartTimeUnix), logger);
        return process is not null;
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration[KnownConfigNames.CliProcessId] is not { } pidString || !int.TryParse(pidString, out var pid))
        {
            // If there is no PID environment variable, we assume that the process is not a child process
            // of the Aspire CLI and we won't continue monitoring.
            logger.LogDebug("No CLI process ID configured. Orphan detection disabled.");
            return;
        }

        logger.LogDebug("Starting orphan detection for CLI process {Pid}.", pid);

        // Try to get the CLI process start time for robust orphan detection
        long? expectedStartTimeUnix = null;
        if (configuration[KnownConfigNames.CliProcessStarted] is { } startTimeString &&
            long.TryParse(startTimeString, out var startTimeUnix))
        {
            expectedStartTimeUnix = startTimeUnix;
            logger.LogDebug("Using start time verification. Expected start time: {StartTime}.", expectedStartTimeUnix);
        }
        else
        {
            logger.LogDebug("No valid start time configured. Using PID-only detection.");
        }

        using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);

        logger.LogDebug("Starting detection loop.");
        try
        {
            do
            {
                bool isProcessStillRunning;

                if (expectedStartTimeUnix.HasValue)
                {
                    // Use robust process checking with start time verification
                    isProcessStillRunning = IsProcessRunningWithStartTime(pid, expectedStartTimeUnix.Value);
                }
                else
                {
                    // Fall back to PID-only logic for backwards compatibility
                    isProcessStillRunning = IsProcessRunning(pid);
                }

                if (!isProcessStillRunning)
                {
                    logger.LogDebug("CLI process {Pid} is no longer running. Stopping application.", pid);
                    lifetime.StopApplication();
                    return;
                }
            } while (await periodic.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (TaskCanceledException)
        {
            // This is expected when the app is shutting down.
            logger.LogDebug("Orphan detection cancelled.");
        }
    }
}
