// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost;

internal sealed class OrphanDetector : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OrphanDetector> _logger;

    public OrphanDetector(IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<OrphanDetector> logger)
    {
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    // PID-only liveness check. Used as a fallback when no start time was supplied (older CLIs).
    internal Func<int, bool> IsProcessRunning { get; set; } = static pid => ProcessStartTimeHelper.IsProcessRunning(pid);

    // PID + start-time liveness check. Verifying the start time prevents a recycled PID from making
    // an orphaned server believe its long-dead parent is still alive (the cause of leaked
    // aspire-managed processes under high process churn).
    internal Func<int, long, bool> IsProcessRunningWithStartTime { get; set; } = static (pid, expectedStartTimeUnix) => ProcessStartTimeHelper.IsProcessRunning(pid, expectedStartTimeUnix);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_configuration[KnownConfigNames.RemoteAppHostProcessId] is not { } pidString || !int.TryParse(pidString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                // If there is no PID environment variable, we assume that the process is not a child process
                // of the Aspire CLI and we won't continue monitoring.
                _logger.LogDebug("No parent PID specified, orphan detection disabled");
                return;
            }

            _logger.LogDebug("Monitoring parent process PID: {ParentPid}", pid);

            // Prefer REMOTE_APP_HOST_STARTED but fall back to ASPIRE_CLI_STARTED (the launcher sets both
            // to the same value). When neither is present we degrade to PID-only detection.
            long? expectedStartTimeUnix = null;
            var startTimeString = _configuration[KnownConfigNames.RemoteAppHostProcessStarted]
                ?? _configuration[KnownConfigNames.CliProcessStarted];
            if (ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(startTimeString) is { } parsedStartTime)
            {
                expectedStartTimeUnix = parsedStartTime;
                _logger.LogDebug("Using start time verification. Expected start time: {StartTime}", expectedStartTimeUnix);
            }
            else
            {
                _logger.LogDebug("No valid start time configured. Using PID-only detection.");
            }

            using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1), TimeProvider.System);

            do
            {
                var isProcessStillRunning = expectedStartTimeUnix is { } expected
                    ? IsProcessRunningWithStartTime(pid, expected)
                    : IsProcessRunning(pid);

                if (!isProcessStillRunning)
                {
                    _logger.LogWarning("Parent process {ParentPid} is no longer running, shutting down...", pid);
                    _lifetime.StopApplication();
                    return;
                }
            } while (await periodic.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // This is expected when the app is shutting down.
            _logger.LogDebug("OrphanDetector: Stopped");
        }
    }
}
