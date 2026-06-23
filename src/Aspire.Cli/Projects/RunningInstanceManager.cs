// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Manages running AppHost instances when the socket path is already known.
/// Used for stopping instances during startup conflicts or after user selection.
/// For discovering and selecting AppHosts, use <see cref="Backchannel.AppHostConnectionResolver"/> instead.
/// </summary>
internal sealed class RunningInstanceManager
{
    private const int ProcessTerminationTimeoutMs = 10000; // Wait up to 10 seconds for processes to terminate
    private const int ProcessTerminationPollIntervalMs = 250; // Check process status every 250ms

    private readonly ILogger _logger;
    private readonly IInteractionService _interactionService;
    private readonly TimeProvider _timeProvider;
    private readonly ProfilingTelemetry _profilingTelemetry;

    public RunningInstanceManager(
        ILogger logger,
        IInteractionService interactionService,
        TimeProvider timeProvider,
        ProfilingTelemetry profilingTelemetry)
    {
        _logger = logger;
        _interactionService = interactionService;
        _timeProvider = timeProvider;
        _profilingTelemetry = profilingTelemetry;
    }

    /// <summary>
    /// Stops a running AppHost instance by connecting to its auxiliary backchannel.
    /// </summary>
    /// <param name="socketPath">The path to the auxiliary backchannel socket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the instance was stopped successfully, false otherwise.</returns>
    public async Task<bool> StopRunningInstanceAsync(string socketPath, CancellationToken cancellationToken)
    {
        try
        {
            // Connect to the auxiliary backchannel
            using var backchannel = await AppHostAuxiliaryBackchannel.ConnectAsync(socketPath, _logger, _profilingTelemetry, cancellationToken).ConfigureAwait(false);

            // Get the AppHost information
            var appHostInfo = backchannel.AppHostInfo;

            if (appHostInfo is null)
            {
                _logger.LogWarning("Failed to get AppHost information from running instance");
                return false;
            }

            // Display message that we're stopping the previous instance
            var cliPidText = appHostInfo.CliProcessId.HasValue ? appHostInfo.CliProcessId.Value.ToString(CultureInfo.InvariantCulture) : "N/A";
            _interactionService.DisplayMessage(KnownEmojis.StopSign, $"Stopping previous instance (AppHost PID: {appHostInfo.ProcessId.ToString(CultureInfo.InvariantCulture)}, CLI PID: {cliPidText})");

            var stopped = await StopAndMonitorAsync(backchannel, cancellationToken).ConfigureAwait(false);

            if (stopped)
            {
                _interactionService.DisplaySuccess(RunCommandStrings.RunningInstanceStopped);
                // Clean up the socket file now that the instance has been stopped so a later command does not
                // rediscover it and try to connect to a dead process (https://github.com/microsoft/aspire/issues/17587).
                AppHostHelper.TryDeleteSocketFile(socketPath, _logger);
            }
            else
            {
                _logger.LogWarning("Failed to stop running instance within timeout");
            }

            return stopped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop running instance");
            return false;
        }
    }

    /// <summary>
    /// Sends an RPC stop to the AppHost via its auxiliary backchannel and waits for the AppHost
    /// and child CLI processes to terminate within the standard timeout. Used by callers that
    /// already hold an established backchannel (e.g., right after launching the AppHost) so the
    /// same "RPC stop + wait for termination" sequence is shared with socket-discovered stops.
    /// </summary>
    /// <param name="backchannel">An open auxiliary backchannel to the AppHost.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all monitored processes terminated within the timeout, false otherwise.</returns>
    public async Task<bool> StopAndMonitorAsync(IAppHostAuxiliaryBackchannel backchannel, CancellationToken cancellationToken)
    {
        var appHostInfo = backchannel.AppHostInfo;
        if (appHostInfo is null)
        {
            _logger.LogWarning("Failed to get AppHost information from running instance");
            return false;
        }

        await backchannel.StopAppHostAsync(cancellationToken).ConfigureAwait(false);
        return await MonitorProcessesForTerminationAsync(appHostInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Monitors a set of processes for termination within a timeout period.
    /// </summary>
    /// <param name="appHostInfo">Information about the AppHost processes to monitor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all processes terminated within the timeout, false otherwise.</returns>
    public async Task<bool> MonitorProcessesForTerminationAsync(AppHostInformation appHostInfo, CancellationToken cancellationToken)
    {
        var startTime = _timeProvider.GetUtcNow();
        var pidsToMonitor = new List<int> { appHostInfo.ProcessId };

        if (appHostInfo.CliProcessId.HasValue)
        {
            pidsToMonitor.Add(appHostInfo.CliProcessId.Value);
        }

        while ((_timeProvider.GetUtcNow() - startTime).TotalMilliseconds < ProcessTerminationTimeoutMs)
        {
            var allStopped = true;

            foreach (var pid in pidsToMonitor)
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    // If we can get the process, it's still running
                    allStopped = false;
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist, it has stopped
                }
            }

            if (allStopped)
            {
                return true;
            }

            await Task.Delay(ProcessTerminationPollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        // Timeout reached
        return false;
    }
}
