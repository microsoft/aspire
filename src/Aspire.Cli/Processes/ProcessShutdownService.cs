// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// Coordinates graceful process shutdown requests, termination monitoring, and force-kill fallback.
/// </summary>
internal sealed class ProcessShutdownService(
    ILayoutDiscovery layoutDiscovery,
    LayoutProcessRunner layoutProcessRunner,
    CliExecutionContext executionContext,
    ILogger<ProcessShutdownService> logger,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan s_processTerminationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_processTerminationPollInterval = TimeSpan.FromMilliseconds(250);

    public Task<bool> StopProcessTreeAsync(
        int pid,
        DateTimeOffset? startTime,
        bool includeStartTimeForDcp,
        CancellationToken cancellationToken)
    {
        return StopProcessesAsync(
            [new ProcessTarget(pid, startTime)],
            token => RequestProcessTreeGracefulShutdownAsync(pid, startTime, includeStartTimeForDcp, token),
            cancellationToken);
    }

    public async Task<bool> StopAppHostAsync(
        AppHostInformation? appHostInfo,
        Func<CancellationToken, Task<bool>>? requestRpcStopAsync,
        CancellationToken cancellationToken)
    {
        if (appHostInfo is null)
        {
            return requestRpcStopAsync is not null && await TryRequestRpcStopAsync(requestRpcStopAsync, cancellationToken).ConfigureAwait(false);
        }

        var processesToMonitor = new List<ProcessTarget> { new(appHostInfo.ProcessId, appHostInfo.StartedAt) };
        if (appHostInfo.CliProcessId is int cliPid)
        {
            processesToMonitor.Add(new ProcessTarget(cliPid, appHostInfo.CliStartedAt));
        }

        return await StopProcessesAsync(
            processesToMonitor,
            token => RequestAppHostGracefulShutdownAsync(appHostInfo, requestRpcStopAsync, token),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> StopProcessesAsync(
        IReadOnlyCollection<ProcessTarget> processesToMonitorAndKill,
        Func<CancellationToken, Task<bool>> requestGracefulShutdownAsync,
        CancellationToken cancellationToken)
    {
        var gracefulShutdownRequested = await TryRequestGracefulShutdownAsync(requestGracefulShutdownAsync, cancellationToken).ConfigureAwait(false);
        if (gracefulShutdownRequested && await MonitorProcessesForTerminationAsync(processesToMonitorAndKill, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        foreach (var process in processesToMonitorAndKill.Distinct())
        {
            logger.LogWarning("Process {Pid} did not stop gracefully within timeout. Forcing process to terminate.", process.Pid);
            ProcessSignaler.ForceKill(process.Pid, process.StartTime, logger);
        }

        return await MonitorProcessesForTerminationAsync(processesToMonitorAndKill, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RequestAppHostGracefulShutdownAsync(
        AppHostInformation appHostInfo,
        Func<CancellationToken, Task<bool>>? requestRpcStopAsync,
        CancellationToken cancellationToken)
    {
        if (appHostInfo.CliProcessId is int cliPid)
        {
            logger.LogDebug("Requesting AppHost process tree shutdown via root CLI PID {Pid}", cliPid);
            // CliStartedAt is recorded with second-level precision, so validate it locally with tolerance
            // instead of passing it to DCP's millisecond-precision process-start-time option.
            return await RequestProcessTreeGracefulShutdownAsync(cliPid, appHostInfo.CliStartedAt, includeStartTimeForDcp: false, cancellationToken).ConfigureAwait(false);
        }

        if (requestRpcStopAsync is not null && await TryRequestRpcStopAsync(requestRpcStopAsync, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        logger.LogDebug("RPC stop not available, requesting shutdown via AppHost PID {Pid}", appHostInfo.ProcessId);
        return await RequestProcessTreeGracefulShutdownAsync(appHostInfo.ProcessId, appHostInfo.StartedAt, includeStartTimeForDcp: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRequestGracefulShutdownAsync(
        Func<CancellationToken, Task<bool>> requestGracefulShutdownAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await requestGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to request graceful process shutdown.");
            return false;
        }
    }

    private async Task<bool> TryRequestRpcStopAsync(Func<CancellationToken, Task<bool>> requestRpcStopAsync, CancellationToken cancellationToken)
    {
        try
        {
            return await requestRpcStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send stop signal via RPC");
            return false;
        }
    }

    private async Task<bool> RequestProcessTreeGracefulShutdownAsync(
        int pid,
        DateTimeOffset? startTime,
        bool includeStartTimeForDcp,
        CancellationToken cancellationToken)
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, startTime, logger);
        if (process is null)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return await TryStopProcessTreeWithDcpAsync(pid, startTime, includeStartTimeForDcp, cancellationToken).ConfigureAwait(false);
        }

        logger.LogDebug("Sending stop signal to process {Pid}", pid);
        ProcessSignaler.RequestGracefulShutdown(pid, startTime, logger);
        return true;
    }

    internal async Task<bool> TryStopProcessTreeWithDcpAsync(int pid, DateTimeOffset? startTime, bool includeStartTime, CancellationToken cancellationToken)
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, startTime, logger);
        if (process is null)
        {
            return true;
        }

        var dcpDirectory = layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
        if (dcpDirectory is null)
        {
            logger.LogWarning("Could not find DCP in the Aspire layout.");
            return false;
        }

        var dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
        if (!File.Exists(dcpPath))
        {
            logger.LogWarning("Could not find DCP executable at '{DcpPath}'.", dcpPath);
            return false;
        }

        // Ensure we only stop the target process and not all children to allow DCP to avoid accidentally killing the child DCP instance.
        var arguments = new List<string>
        {
            "stop-process-tree",
            "--skip-descendants",
            "--pid",
            pid.ToString(CultureInfo.InvariantCulture)
        };

        if (includeStartTime && startTime is not null)
        {
            arguments.Add("--process-start-time");
            arguments.Add(FormatDcpProcessStartTime(startTime.Value));
        }

        var (exitCode, output, error) = await layoutProcessRunner.RunAsync(
            dcpPath,
            arguments,
            workingDirectory: executionContext.WorkingDirectory.FullName,
            ct: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(output))
        {
            logger.LogDebug("DCP stop-process-tree stdout: {Output}", output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            logger.LogDebug("DCP stop-process-tree stderr: {Error}", error.Trim());
        }

        if (exitCode != 0)
        {
            logger.LogWarning("DCP stop-process-tree exited with code {ExitCode}.", exitCode);
            return false;
        }

        return true;
    }

    private async Task<bool> MonitorProcessesForTerminationAsync(IReadOnlyCollection<ProcessTarget> processes, CancellationToken cancellationToken)
    {
        var startTime = timeProvider.GetUtcNow();
        while (timeProvider.GetUtcNow() - startTime < s_processTerminationTimeout)
        {
            if (processes.All(IsProcessStopped))
            {
                return true;
            }

            await Task.Delay(s_processTerminationPollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private bool IsProcessStopped(ProcessTarget process)
    {
        using var runningProcess = ProcessSignaler.TryGetRunningProcess(process.Pid, process.StartTime, logger);
        return runningProcess is null;
    }

    internal static string FormatDcpProcessStartTime(DateTimeOffset startTime)
    {
        return startTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    internal readonly record struct ProcessTarget(int Pid, DateTimeOffset? StartTime);
}
